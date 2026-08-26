using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The half of `20-02` the item is named after: a tenant editing an already-generated day directly,
/// and those edits surviving the next materialisation run. Against a real Postgres, because the
/// mechanism that makes them survive is a row plus a constraint, not a flag in memory.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ManualDayEditingTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Monday = new(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Tuesday = new(2026, 5, 5);

    [Fact]
    public async Task DeleteDayOff_RemovesEveryAvailableSlot_AndLeavesOneBlockingRowInTheirPlace()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        var result = await harness.DeleteDayOffAsync(seed, Tuesday);

        Assert.True(result.IsSuccess);

        var day = await ReadDayAsync(seed, Tuesday);
        var blocking = Assert.Single(day);
        Assert.Equal(EventStatus.Blocked, blocking.Status);

        // The blocking row spans what it replaced - the worker's Tuesday, not the whole calendar
        // day. A worker whose Tuesday was 09:00-18:00 is not thereby unavailable at midnight.
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 6, 0, 0, TimeSpan.Zero), blocking.StartsAt.ToUniversalTime());
        Assert.Null(blocking.CustomerId);

        // Neighbouring days are untouched: the edit is scoped by local_date, which is the column
        // adr/0049 stored rather than derived precisely so that this is one indexed predicate.
        Assert.NotEmpty(await ReadDayAsync(seed, Tuesday.AddDays(1)));
    }

    [Fact]
    public async Task DeleteDayOff_IsRejected_WhenTheDayAlreadyHasABooking()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        await ClaimOneSlotOnAsync(seed, Tuesday);

        var result = await harness.DeleteDayOffAsync(seed, Tuesday);

        Assert.True(result.IsFailure);
        Assert.Equal("availability.day_has_bookings", result.Error!.Value.Code);

        // Nothing was written: the day still has every slot it had, including the claimed one.
        var day = await ReadDayAsync(seed, Tuesday);
        Assert.Contains(day, e => e.Status == EventStatus.PendingConfirmation);
        Assert.DoesNotContain(day, e => e.Status == EventStatus.Blocked);
    }

    [Fact]
    public async Task DeleteDayOff_IsRejected_WhenTheDayHasNotBeenMaterializedYet()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        // Well past the six-day horizon the harness generated.
        var result = await harness.DeleteDayOffAsync(seed, new DateOnly(2026, 9, 1));

        // Succeeding here would be a lie: with no row to leave behind, the next materialisation run
        // would fill the day in, and the tenant would have been told they were closed when they were
        // not. Declaring a day off past the horizon needs a durable statement the materialiser
        // reads, which this item deliberately does not build.
        Assert.True(result.IsFailure);
        Assert.Equal("availability.day_not_materialized", result.Error!.Value.Code);
    }

    [Fact]
    public async Task DeleteDayOff_IsIdempotent()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        Assert.True((await harness.DeleteDayOffAsync(seed, Tuesday)).IsSuccess);
        Assert.True((await harness.DeleteDayOffAsync(seed, Tuesday)).IsSuccess);

        // A retried request or a double-clicked button must not leave two blocking rows - which the
        // exclusion constraint would have refused anyway, as a 500 rather than a success.
        Assert.Single(await ReadDayAsync(seed, Tuesday));
    }

    [Fact]
    public async Task ADayOff_IsNotResurrectedByTheNextMaterializationRun()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        await harness.DeleteDayOffAsync(seed, Tuesday);

        // The run that would have undone it if a day off were an absence rather than a row.
        var rerun = await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);

        Assert.Equal(0, rerun.SlotsInserted);

        var day = await ReadDayAsync(seed, Tuesday);
        Assert.Equal(EventStatus.Blocked, Assert.Single(day).Status);
    }

    [Fact]
    public async Task EditDayBoundary_RegeneratesTheDayBetweenTheNewWallClockTimes()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        var result = await harness.EditDayBoundaryAsync(seed, Tuesday, new TimeOnly(12, 0), new TimeOnly(15, 0));

        Assert.True(result.IsSuccess);

        var day = await ReadDayAsync(seed, Tuesday);

        // 12:00 Moscow is 09:00Z; 45 + 10 = 55 minutes of stride fits three slots into three hours.
        // The whole day was rebuilt from the same SlotGrid the materialiser uses, so a hand-edited
        // Tuesday has the same shape as a generated Wednesday - buffer included.
        Assert.Equal(3, day.Count);
        Assert.All(day, e => Assert.Equal(EventStatus.Available, e.Status));
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero), day[0].StartsAt.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(55), day[1].StartsAt - day[0].StartsAt);
    }

    [Fact]
    public async Task EditDayBoundary_IsRejected_WhenTheDayAlreadyHasABooking()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        await ClaimOneSlotOnAsync(seed, Tuesday);

        var result = await harness.EditDayBoundaryAsync(seed, Tuesday, new TimeOnly(12, 0), new TimeOnly(15, 0));

        Assert.True(result.IsFailure);
        Assert.Equal("availability.day_has_bookings", result.Error!.Value.Code);
        Assert.Contains(await ReadDayAsync(seed, Tuesday), e => e.Status == EventStatus.PendingConfirmation);
    }

    [Fact]
    public async Task AnEditedDayBoundary_IsNotRewrittenByTheNextMaterializationRun()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        await harness.EditDayBoundaryAsync(seed, Tuesday, new TimeOnly(12, 0), new TimeOnly(15, 0));

        var rerun = await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);

        Assert.Equal(0, rerun.SlotsInserted);

        // Still the shortened day. Nothing marks it as edited - the run skips it because it has
        // rows, which is the same reason it skips every other day it has already generated.
        var day = await ReadDayAsync(seed, Tuesday);
        Assert.Equal(3, day.Count);
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero), day[0].StartsAt.ToUniversalTime());
    }

    [Fact]
    public async Task EditDayBoundary_UndoesADayOff()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        await harness.DeleteDayOffAsync(seed, Tuesday);

        var result = await harness.EditDayBoundaryAsync(seed, Tuesday, new TimeOnly(10, 0), new TimeOnly(13, 0));

        // Deliberate, and v1's only way back: a blocked row has no customer attached by
        // construction, so replacing it strands nobody. Rebuilding a day whose blocking row had
        // stayed would have been refused by the exclusion constraint instead.
        Assert.True(result.IsSuccess);

        var day = await ReadDayAsync(seed, Tuesday);
        Assert.All(day, e => Assert.Equal(EventStatus.Available, e.Status));
        Assert.Equal(3, day.Count);
    }

    [Fact]
    public async Task EditDayBoundary_RejectsAWindowThatClosesBeforeItOpens()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        var result = await harness.EditDayBoundaryAsync(seed, Tuesday, new TimeOnly(15, 0), new TimeOnly(12, 0));

        Assert.True(result.IsFailure);
        Assert.Equal("availability.invalid_day_boundary", result.Error!.Value.Code);
        Assert.NotEmpty(await ReadDayAsync(seed, Tuesday));
    }

    private async Task<(SeededTenant Seed, AvailabilityHarness Harness)> AMaterializedWeekAsync()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);
        return (seed, harness);
    }

    /// <summary>Constructs "this day already has a booking" by calling <c>Event.Claim</c> and saving
    /// through the repository. `20-03` owns the real booking handler and does not exist yet; what
    /// these tests need is only that the row is genuinely no longer <c>Available</c> on disk, which
    /// a load-mutate-save produces exactly as a real claim will.</summary>
    private async Task ClaimOneSlotOnAsync(SeededTenant seed, DateOnly localDate)
    {
        await using var db = fixture.CreateDbContext();
        var target = await db.Events
            .Where(e => e.WorkerId == seed.Worker.Id && e.LocalDate == localDate && e.Status == EventStatus.Available)
            .OrderBy(e => e.StartsAt)
            .FirstAsync();

        target.Claim(seed.Customer.Id, seed.Service.Id, Monday, Monday.AddMinutes(15));
        await new EventRepository(db).SaveAsync(target, CancellationToken.None);
    }

    private async Task<List<Event>> ReadDayAsync(SeededTenant seed, DateOnly localDate)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Events
            .Where(e => e.WorkerId == seed.Worker.Id && e.LocalDate == localDate)
            .OrderBy(e => e.StartsAt)
            .ToListAsync();
    }
}
