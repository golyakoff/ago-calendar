using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-04`'s sweep, against a real Postgres: the deadline boundary, the atomicity of
/// claim-plus-transition-plus-outbox, and the health count that keeps a dead sweep from being silent.
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConfirmationSweepTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Now.AddMinutes(15);

    [Fact]
    public async Task ADeadlineThatHasPassed_IsConfirmed_AndDoingNothingIsWhatConfirmedIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed, "+79997000001");

        var confirmed = await SweepAsync(seed.Tenant.Id, at: Deadline);

        Assert.Equal(1, confirmed);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == booking.Id);

        Assert.Equal(EventStatus.Booked, stored.Status);

        // Cleared by Event.Confirm: a deadline that has been honoured is not a deadline any more, and
        // leaving it set would keep the row matching the sweep's own claim predicate forever.
        Assert.Null(stored.ConfirmationDeadline);

        // The customer is unchanged - nobody was told anything new, and nothing about the lead card
        // is part of confirming.
        Assert.Equal(booking.CustomerId, stored.CustomerId);
    }

    [Fact]
    public async Task TheDeadlineBoundaryIsExact_OneSecondBeforeIsUntouched_OneSecondAfterIsSwept()
    {
        // The one place in this product where a clock decides an outcome (CLAUDE.md rule 11), so the
        // boundary is asserted rather than assumed - and asserted from both sides, because a `<`
        // written where `<=` was meant is invisible from either side alone.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed, "+79997000002");

        Assert.Equal(0, await SweepAsync(seed.Tenant.Id, at: Deadline.AddSeconds(-1)));
        Assert.Equal(EventStatus.PendingConfirmation, await StatusOfAsync(booking.Id));

        // Exactly on the deadline counts as expired: the window is closed at the instant it closes,
        // not one tick later. `<=`, and this is the assertion that pins it.
        Assert.Equal(1, await SweepAsync(seed.Tenant.Id, at: Deadline));
        Assert.Equal(EventStatus.Booked, await StatusOfAsync(booking.Id));
    }

    [Fact]
    public async Task ASecondSweepAfterTheFirst_ConfirmsNothingAndStagesNothing()
    {
        // Idempotent by construction rather than by a guard: a confirmed row no longer matches
        // `status = 'PendingConfirmation'`, so the claim simply does not see it. The same shape every
        // other claim in this product uses - the predicate is the mechanism.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await APendingBookingAsync(seed, "+79997000003");

        Assert.Equal(1, await SweepAsync(seed.Tenant.Id, at: Deadline));
        Assert.Equal(0, await SweepAsync(seed.Tenant.Id, at: Deadline.AddHours(1)));

        Assert.Single(await OutboxRowsAsync(seed.Tenant.Id));
    }

    [Fact]
    public async Task TheOutboxRowIsWrittenInTheSameTransactionAsTheTransition()
    {
        // The Done-when asks for this to be proven by inspecting the committed transaction rather
        // than the end state. Postgres stamps every row with the transaction that created its current
        // version (`xmin`), so two rows written by one transaction carry the same value - which is a
        // direct observation of atomicity, not an inference from both happening to be present.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed, "+79997000004");

        await SweepAsync(seed.Tenant.Id, at: Deadline);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select (select xmin::text from events where id = @eventId),
                   (select xmin::text from outbox where id = @eventId)
            """,
            connection);
        command.Parameters.AddWithValue("eventId", booking.Id.Value);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var eventTransaction = reader.GetString(0);
        var outboxTransaction = reader.GetString(1);

        Assert.Equal(eventTransaction, outboxTransaction);
    }

    [Fact]
    public async Task TheStagedEventCarriesWhatTwentyFiveNeeds_AndNoPhoneNumber()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed, "+79997000005");

        await SweepAsync(seed.Tenant.Id, at: Deadline);

        var row = Assert.Single(await OutboxRowsAsync(seed.Tenant.Id));
        Assert.Equal(nameof(BookingConfirmed), row.Type);

        // Partitioned per booking, not per tenant: the only ordering that matters is between events
        // about one booking, and a tenant-wide key would serialise a busy shop's confirmations behind
        // each other for a guarantee nobody needs.
        Assert.Equal(booking.Id.Value.ToString(), row.PartitionKey);

        var payload = JsonSerializer.Deserialize<BookingConfirmed>(row.Payload)!;
        Assert.Equal(booking.Id.Value, payload.EventId);
        Assert.Equal(seed.Tenant.Id.Value, payload.TenantId);
        Assert.Equal(seed.Calendar.Id.Value, payload.CalendarId);
        // The booking's own customer, which this fixture created for the phone number under test -
        // not the seed's default one.
        Assert.Equal(booking.CustomerId!.Value.Value, payload.CustomerId);
        Assert.Equal(booking.StartsAt, payload.StartsAt);
        Assert.Equal(booking.LocalDate, payload.LocalDate);

        // The rule this event exists under: it crosses a broker to consumers this product does not
        // control, and it lands in a table nothing prunes. `20-05` resolves the phone from CustomerId
        // at send time; a copy here would be personal data outliving the row it came from.
        Assert.DoesNotContain("7999", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("phone", row.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARejectedBooking_IsNeverSwept()
    {
        // The operator won the race. The claim's predicate names PendingConfirmation, so a cancelled
        // row is invisible to it - no interlock between the handler and the sweep is needed or
        // present.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed, "+79997000006");

        await using (var db = fixture.CreateDbContext())
        {
            var loaded = await db.Events.SingleAsync(e => e.Id == booking.Id);
            loaded.Reject(Now.AddMinutes(1));
            loaded.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await SweepAsync(seed.Tenant.Id, at: Deadline));
        Assert.Equal(EventStatus.Cancelled, await StatusOfAsync(booking.Id));
        Assert.Empty(await OutboxRowsAsync(seed.Tenant.Id));
    }

    [Fact]
    public async Task AnotherTenantsExpiredBooking_IsNotSwept()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);
        var theirBooking = await APendingBookingAsync(theirs, "+79997000007");

        Assert.Equal(0, await SweepAsync(mine.Tenant.Id, at: Deadline));
        Assert.Equal(EventStatus.PendingConfirmation, await StatusOfAsync(theirBooking.Id));
    }

    [Fact]
    public async Task ABacklogLargerThanOneBatch_DrainsInDeadlineOrder()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var bookings = new List<Event>();
        for (var i = 0; i < 5; i++)
        {
            // A distinct slot each - one worker cannot hold five overlapping events, and the
            // exclusion constraint says so (adr/0049).
            bookings.Add(await APendingBookingAsync(
                seed, $"+7999710000{i}", deadline: Deadline.AddMinutes(i), startsAt: Now.AddDays(3).AddHours(i)));
        }

        // Batch size two, so the first transaction can only take the two oldest deadlines.
        var confirmed = await SweepAsync(seed.Tenant.Id, at: Deadline.AddHours(1), batchSize: 2, drain: false);
        Assert.Equal(2, confirmed);

        var statuses = await Task.WhenAll(bookings.Select(b => StatusOfAsync(b.Id)));

        // Oldest deadline first: a backlog drains in the order it accumulated, so nothing is starved
        // by newer bookings arriving.
        Assert.Equal(
            [EventStatus.Booked, EventStatus.Booked, EventStatus.PendingConfirmation,
             EventStatus.PendingConfirmation, EventStatus.PendingConfirmation],
            statuses);
    }

    [Fact]
    public async Task TheOverdueCount_IsZeroAfterASweepAndNonZeroWhenTheSweepHasNotRun()
    {
        // The number that keeps a dead sweep from being silent. Doing nothing confirms a booking, so
        // a job that stops running fails invisibly - this counts the outcome rather than the loop, so
        // it climbs whether the loop died, the query broke, or the transaction never committed.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await APendingBookingAsync(seed, "+79997000008");

        await using (var db = fixture.CreateDbContext())
        {
            var confirmer = Confirmer(db);

            // Before any sweep: one booking is past its deadline and still pending. This is exactly
            // what an operator would see flagged overdue on the queue screen.
            Assert.Equal(1, await confirmer.CountOverdueAsync(seed.Tenant.Id, Deadline, CancellationToken.None));

            // And not overdue a second earlier - the count uses the same boundary as the claim.
            Assert.Equal(0, await confirmer.CountOverdueAsync(
                seed.Tenant.Id, Deadline.AddSeconds(-1), CancellationToken.None));
        }

        await SweepAsync(seed.Tenant.Id, at: Deadline);

        await using var reader = fixture.CreateDbContext();
        Assert.Equal(0, await Confirmer(reader).CountOverdueAsync(
            seed.Tenant.Id, Deadline.AddHours(1), CancellationToken.None));
    }

    private static ExpiredBookingConfirmer Confirmer(
        Ago.Calendar.Infrastructure.Postgres.Persistence.AgoCalendarDbContext db) =>
        new(db, new EfOutboxWriter<Ago.Calendar.Infrastructure.Postgres.Persistence.AgoCalendarDbContext>(db),
            new UuidV7Generator());

    private async Task<int> SweepAsync(
        TenantId tenantId, DateTimeOffset at, int batchSize = 100, bool drain = true)
    {
        var total = 0;
        int batch;
        do
        {
            await using var db = fixture.CreateDbContext();
            batch = await Confirmer(db).ConfirmExpiredAsync(tenantId, at, batchSize, CancellationToken.None);
            total += batch;
        } while (drain && batch == batchSize);

        return total;
    }

    private async Task<EventStatus> StatusOfAsync(EventId eventId)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.Events.SingleAsync(e => e.Id == eventId)).Status;
    }

    private async Task<IReadOnlyList<OutboxRow>> OutboxRowsAsync(TenantId tenantId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select o.type, o.partition_key, o.payload
            from outbox o
            join events e on e.id = o.id
            where e.tenant_id = @tenantId
            order by o.occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private async Task<Event> APendingBookingAsync(
        SeededTenant seed, string phone, DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null)
    {
        var slot = CalendarSeed.Slot(seed, startsAt ?? Now.AddDays(3));

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);

        var customer = await new CustomerRepository(db).FindByPhoneAsync(
            seed.Tenant.Id, new PhoneNumber(phone), CancellationToken.None);
        if (customer is null)
        {
            customer = Customer.Register(
                new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, new PhoneNumber(phone), Now);
            await new CustomerRepository(db).AddAsync(customer, CancellationToken.None);
        }

        // Constructed through the aggregate rather than through `20-03`'s BookingStore: what these
        // tests need is a row that is genuinely PendingConfirmation with a known deadline, and going
        // through the real claim would tie every one of them to that item's own clock handling.
        slot.Claim(customer.Id, seed.Service.Id, Now, deadline ?? Deadline);
        slot.ClearDomainEvents();
        await new EventRepository(db).SaveAsync(slot, CancellationToken.None);

        return slot;
    }

    private sealed record OutboxRow(string Type, string PartitionKey, string Payload);
}
