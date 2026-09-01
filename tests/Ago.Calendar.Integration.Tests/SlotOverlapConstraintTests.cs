using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The overlap guarantee, proven against a real Postgres rather than asserted from the migration
/// text. A unit test cannot prove this: the rule is about two rows, and the whole question is what
/// happens when two writers reach for the same time at once - which needs two genuinely concurrent
/// transactions, not two sequential awaits.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SlotOverlapConstraintTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset SlotStart = new(2026, 3, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoConcurrentWriters_ForTheSameWorkerAndTime_LeaveExactlyOneRow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // Two real connections, two real transactions, both open at the same time - the first holds
        // the range, the second blocks on it, and Postgres decides. This is what the exclusion
        // constraint is for and the only way to show it doing its job.
        await using var firstConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var secondConnection = await fixture.DataSource.OpenConnectionAsync();

        var firstTransaction = await firstConnection.BeginTransactionAsync();
        var secondTransaction = await secondConnection.BeginTransactionAsync();

        await using var firstDb = fixture.CreateDbContext(firstConnection);
        await using var secondDb = fixture.CreateDbContext(secondConnection);
        await firstDb.Database.UseTransactionAsync(firstTransaction);
        await secondDb.Database.UseTransactionAsync(secondTransaction);

        // Not identical rows - overlapping ones. A unique index could never catch this pair: the
        // values differ, only the intervals collide.
        var first = CalendarSeed.Slot(seed, SlotStart);
        var second = CalendarSeed.Slot(seed, SlotStart.AddMinutes(30));

        await new EventRepository(firstDb).AddRangeAsync([first], CancellationToken.None);

        // The second insert cannot complete while the first transaction is open: an exclusion
        // constraint makes the second writer wait on the first, exactly like a unique index would.
        var blocked = new EventRepository(secondDb).AddRangeAsync([second], CancellationToken.None);
        await WaitUntilWaitingAsync(secondConnection.ProcessID);

        await firstTransaction.CommitAsync();

        // ...and once the winner commits, the loser is refused rather than silently allowed through.
        var overlap = await Assert.ThrowsAsync<SlotOverlapException>(() => blocked);
        Assert.Equal(seed.Worker.Id, overlap.WorkerId);
        Assert.Equal(
            PostgresErrorCodes.ExclusionViolation,
            Assert.IsType<PostgresException>(overlap.InnerException?.InnerException).SqlState);

        await secondTransaction.RollbackAsync();

        await using var reader = fixture.CreateDbContext();
        var stored = reader.Events.Where(e => e.WorkerId == seed.Worker.Id).ToList();
        var survivor = Assert.Single(stored);
        Assert.Equal(first.Id, survivor.Id);
    }

    [Fact]
    public async Task BackToBackSlots_DoNotCollide()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // The half-open bound, end to end: TimeSlot says these are adjacent and so does the
        // constraint's own tstzrange('[)'). If the two ever disagreed, every ordinary working day
        // would fail to materialise past its first slot.
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync(
                [CalendarSeed.Slot(seed, SlotStart), CalendarSeed.Slot(seed, SlotStart.AddMinutes(45))],
                CancellationToken.None);
        }

        await using var reader = fixture.CreateDbContext();
        Assert.Equal(2, reader.Events.Count(e => e.WorkerId == seed.Worker.Id));
    }

    [Fact]
    public async Task ACancelledBooking_StopsBlockingItsTime()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, SlotStart);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EventRepository(db);
            await repository.AddRangeAsync([slot], CancellationToken.None);
            slot.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
            slot.Cancel(CalendarSeed.Now.AddMinutes(1));
            await repository.SaveAsync(slot, CancellationToken.None);
        }

        // The constraint's WHERE (status <> 'Cancelled') is what makes this possible - the time is
        // genuinely free again, so the same slot can be offered a second time.
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([CalendarSeed.Slot(seed, SlotStart)], CancellationToken.None);
        }

        await using var reader = fixture.CreateDbContext();
        Assert.Equal(2, reader.Events.Count(e => e.WorkerId == seed.Worker.Id));
    }

    [Fact]
    public async Task ABlockedSlot_StillOccupiesTheWorker()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using (var db = fixture.CreateDbContext())
        {
            var block = Event.BlockOut(
                new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
                new TimeSlot(SlotStart, SlotStart.AddHours(1)), new DateOnly(2026, 3, 3), CalendarSeed.Now);
            await new EventRepository(db).AddRangeAsync([block], CancellationToken.None);
        }

        await using var second = fixture.CreateDbContext();
        await Assert.ThrowsAsync<SlotOverlapException>(() =>
            new EventRepository(second).AddRangeAsync(
                [CalendarSeed.Slot(seed, SlotStart.AddMinutes(30))], CancellationToken.None));
    }

    [Fact]
    public async Task TwoWorkers_MayHoldTheSameTime()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var second = Worker.Create(new WorkerId(CalendarSeed.NewId()), seed.Tenant.Id, "Bo", "Bo", null, CalendarSeed.Now);
        second.JoinCalendar(seed.Calendar);

        await using (var db = fixture.CreateDbContext())
        {
            db.Workers.Add(second);
            await db.SaveChangesAsync();
        }

        // The constraint is scoped by worker_id, not by calendar: a barbershop with two chairs is
        // the ordinary case, and a rule that forbade it would be a bug of its own.
        await using (var db = fixture.CreateDbContext())
        {
            var mine = CalendarSeed.Slot(seed, SlotStart);
            var theirs = Event.Materialize(
                new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, second.Id,
                new TimeSlot(SlotStart, SlotStart.AddMinutes(45)), new DateOnly(2026, 3, 3), CalendarSeed.Now);
            await new EventRepository(db).AddRangeAsync([mine, theirs], CancellationToken.None);
        }

        await using var reader = fixture.CreateDbContext();
        Assert.Equal(2, reader.Events.Count(e => e.CalendarId == seed.Calendar.Id));
    }

    /// <summary>Polls <c>pg_stat_activity</c> rather than sleeping a guessed interval: the race is
    /// only genuinely closed once the second writer is parked on the first one's lock, and a fixed
    /// delay would be either slower than necessary or occasionally too short.</summary>
    private async Task WaitUntilWaitingAsync(int processId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", processId);
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Process {processId} never parked on a lock.");
    }
}
