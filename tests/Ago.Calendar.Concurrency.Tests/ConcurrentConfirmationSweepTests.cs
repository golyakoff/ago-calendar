using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>
/// Two <c>Ago.Calendar.Worker</c> replicas sweeping the same tenant at the same instant.
///
/// <para><b>What <c>SKIP LOCKED</c> has to give, and what it must not.</b> Two sweepers must split
/// the expired rows rather than race for one - so no booking is confirmed twice, no
/// <c>BookingConfirmed</c> is staged twice, and neither sweeper blocks waiting for the other. That
/// last part is the reason for <c>SKIP LOCKED</c> rather than a plain <c>FOR UPDATE</c>: a plain lock
/// would be correct and would serialise the second replica behind the first for the whole batch,
/// which is the behaviour a second replica exists to avoid.</para>
///
/// <para><b>How contention is forced rather than hoped for.</b> Each sweeper gets its own
/// <c>DbContext</c> on its own pooled connection, <b>opens that connection before</b> parking on a
/// shared gate, and all are released together. Without the pre-open, a connection handshake after
/// release staggers the arrivals across exactly the interval the claim is meant to be tested across,
/// and the test quietly degrades into a sequence. Same technique `20-03`'s booking race uses, for the
/// same reason.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public class ConcurrentConfirmationSweepTests(ConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Now.AddMinutes(15);

    [Fact]
    public async Task TwoSweepersRacingOneExpiredBooking_ProduceExactlyOneConfirmation()
    {
        var seed = await SeedAsync();
        var booking = await APendingBookingAsync(seed, 0);

        var results = await RaceAsync(seed.TenantId, sweepers: 2, batchSize: 10);

        // One sweeper confirmed it; the other found the row locked, skipped it, and returned zero -
        // which is an ordinary tick, not a failure. Nothing threw, and nothing retried.
        Assert.Equal(1, results.Sum());

        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Booked, (await db.Events.SingleAsync(e => e.Id == booking.Id)).Status);

        // And exactly one outbox row, which is the assertion that matters most: a double-staged
        // BookingConfirmed would become two SMS messages to one customer in `20-05`, and neither the
        // event's status nor its row count would have shown it.
        Assert.Equal(1, await OutboxCountAsync(booking.Id));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public async Task ManySweepersRacingManyExpiredBookings_ConfirmEachExactlyOnce(int sweepers)
    {
        var seed = await SeedAsync();
        var bookings = new List<Event>();
        for (var i = 0; i < 20; i++)
        {
            bookings.Add(await APendingBookingAsync(seed, i));
        }

        var results = await RaceAsync(seed.TenantId, sweepers, batchSize: 5);

        // Every booking confirmed, and the total confirmations equals the number of bookings - so the
        // sweepers split the work rather than duplicating it. A batch bound smaller than the backlog
        // is deliberate: it forces several claims per sweeper and therefore several chances to
        // collide.
        Assert.Equal(bookings.Count, results.Sum());

        await using var db = fixture.CreateDbContext();
        var statuses = await db.Events
            .Where(e => e.TenantId == seed.TenantId)
            .Select(e => e.Status)
            .ToListAsync();

        Assert.All(statuses, status => Assert.Equal(EventStatus.Booked, status));

        foreach (var booking in bookings)
        {
            Assert.Equal(1, await OutboxCountAsync(booking.Id));
        }
    }

    [Fact]
    public async Task ASweeperThatFindsEverythingLocked_ReturnsZeroRatherThanBlocking()
    {
        // The property `SKIP LOCKED` exists for, isolated: hold every expired row in an open
        // transaction, then run a sweeper and watch it come back promptly with nothing. A plain
        // `FOR UPDATE` would sit here until the holder committed.
        var seed = await SeedAsync();
        await APendingBookingAsync(seed, 0);

        await using var holder = fixture.CreateDbContext();
        await holder.Database.OpenConnectionAsync();
        await using var holdingTransaction = await holder.Database.BeginTransactionAsync();

        await using (var command = new NpgsqlCommand(
            "select id from events where tenant_id = @t and status = 'PendingConfirmation' for update",
            (NpgsqlConnection)holder.Database.GetDbConnection(),
            (NpgsqlTransaction)holdingTransaction.GetDbTransaction()))
        {
            command.Parameters.AddWithValue("t", seed.TenantId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var swept = await SweepOnceAsync(seed.TenantId, batchSize: 10);
        stopwatch.Stop();

        Assert.Equal(0, swept);

        // Generous by design - this is asserting "did not block on a lock somebody holds
        // indefinitely", not measuring anything. The holding transaction is still open as this runs.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"the sweep waited {stopwatch.Elapsed} for a lock it should have skipped");

        await holdingTransaction.RollbackAsync();
    }

    private async Task<IReadOnlyList<int>> RaceAsync(TenantId tenantId, int sweepers, int batchSize)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = Enumerable.Range(0, sweepers)
            .Select(_ => Task.Run(async () =>
            {
                await using var db = fixture.CreateDbContext();

                // Before the gate, deliberately: a handshake after release would stagger the arrivals
                // across exactly the window the claim is meant to be tested across.
                await db.Database.OpenConnectionAsync();
                var confirmer = Confirmer(db);
                await gate.Task;

                var total = 0;
                int batch;
                do
                {
                    batch = await confirmer.ConfirmExpiredAsync(
                        tenantId, Deadline, batchSize, CancellationToken.None);
                    total += batch;
                } while (batch > 0);

                return total;
            }))
            .ToList();

        await Task.Delay(50);
        gate.SetResult();

        return await Task.WhenAll(runs);
    }

    private async Task<int> SweepOnceAsync(TenantId tenantId, int batchSize)
    {
        await using var db = fixture.CreateDbContext();
        return await Confirmer(db).ConfirmExpiredAsync(tenantId, Deadline, batchSize, CancellationToken.None);
    }

    private static ExpiredBookingConfirmer Confirmer(AgoCalendarDbContext db) =>
        new(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator());

    private async Task<int> OutboxCountAsync(EventId eventId)
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from outbox where id = @id", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("id", eventId.Value);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Event> APendingBookingAsync(SeededCalendar seed, int index)
    {
        var start = Now.AddDays(3).AddHours(index);
        var slot = Event.Materialize(
            new EventId(NewId()), seed.TenantId, seed.CalendarId, seed.WorkerId,
            new TimeSlot(start, start.AddMinutes(45)), DateOnly.FromDateTime(start.UtcDateTime), Now);

        var customer = Customer.Register(
            new CustomerId(NewId()), seed.TenantId, new PhoneNumber($"+799972000{index:D2}"), Now);

        await using var db = fixture.CreateDbContext();
        db.Customers.Add(customer);
        db.Events.Add(slot);
        await db.SaveChangesAsync();

        slot.Claim(customer.Id, seed.ServiceId, Now, Deadline);
        slot.ClearDomainEvents();
        await db.SaveChangesAsync();

        return slot;
    }

    private async Task<SeededCalendar> SeedAsync()
    {
        var tenant = Tenant.Register(new TenantId(NewId()), "Barbershop", new TenantPublicKey("shop-" + NewId().ToString("N")), Now);
        var calendar = BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), 10, Now);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Alex");
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        await db.SaveChangesAsync();

        return new SeededCalendar(tenant.Id, calendar.Id, worker.Id) { ServiceId = service.Id };
    }

    private static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
