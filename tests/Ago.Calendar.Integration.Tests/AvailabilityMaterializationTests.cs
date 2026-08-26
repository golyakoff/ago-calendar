using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-02`'s central claims, against a real Postgres with the real exclusion constraint in place:
/// the run is idempotent, it never touches a row that has moved past <c>Available</c>, and the one
/// wall-clock-to-instant conversion behaves correctly on both sides of a DST transition.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AvailabilityMaterializationTests(PostgresFixture fixture)
{
    // A Monday, chosen so a seven-day horizon covers one of each weekday. UTC, because a clock is
    // an instant - which local day it is depends on the calendar being materialised, and that is
    // exactly the conversion under test.
    private static readonly DateTimeOffset Monday = new(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunningTwice_ProducesExactlyTheSameSlots_AndWritesNothingTheSecondTime()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));

        var first = await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);
        var afterFirstRun = Fingerprint(await ReadSlotsAsync(seed));

        var second = await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);
        var afterSecondRun = Fingerprint(await ReadSlotsAsync(seed));

        Assert.True(first.SlotsInserted > 0, "the first run must actually generate something");

        // The assertion the item is named for. Not "no duplicates were visible" but "the second run
        // wrote zero rows" - a stronger statement, and one that stays true even if a future
        // duplicate happened to be invisible to the query above.
        Assert.Equal(0, second.SlotsInserted);
        Assert.Equal(first.DaysConsidered, second.DaysSkipped);

        // Same rows, same ids, same instants: not merely the same count.
        Assert.Equal(afterFirstRun, afterSecondRun);
    }

    [Fact]
    public async Task AClaimedSlot_SurvivesARepeatedRun_AndIsNotDuplicated()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);

        // The precondition is constructed by calling Event.Claim directly and saving through
        // IEventRepository, not by going through a booking handler - `20-03` builds that handler and
        // does not exist yet at this point in the sequence. What matters for this test is only that
        // the row is genuinely no longer Available in the database, which a load-mutate-save
        // achieves exactly as a real booking will.
        EventId claimedId;
        DateTimeOffset claimedStart;
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EventRepository(db);
            var target = await db.Events
                .Where(e => e.CalendarId == seed.Calendar.Id && e.Status == EventStatus.Available)
                .OrderBy(e => e.StartsAt)
                .Skip(3)
                .FirstAsync();

            target.Claim(seed.Customer.Id, seed.Service.Id, Monday, Monday.AddMinutes(15));
            await repository.SaveAsync(target, CancellationToken.None);
            claimedId = target.Id;
            claimedStart = target.StartsAt;
        }

        // Twice more, not once: "safe to repeat" is a claim about every run, and a job that ran
        // daily for a month would reach this row thirty times.
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);
        var rerun = await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 6);

        await using var reader = fixture.CreateDbContext();
        var claimed = await reader.Events.SingleAsync(e => e.Id == claimedId);

        // The booking is untouched: same status, same customer, same slot.
        Assert.Equal(EventStatus.PendingConfirmation, claimed.Status);
        Assert.Equal(seed.Customer.Id, claimed.CustomerId);

        // And no second row was created for the time it occupies - which is the failure that would
        // let a second customer book the same chair. Asserted against the whole table for that
        // worker rather than against a status, because a duplicate in *any* status is the bug.
        var rowsForThatInstant = await reader.Events
            .CountAsync(e => e.WorkerId == seed.Worker.Id && e.StartsAt == claimedStart);
        Assert.Equal(1, rowsForThatInstant);

        Assert.Equal(0, rerun.SlotsInserted);
    }

    [Fact]
    public async Task SlotsAreSpacedByTheCalendarsBuffer_AndSizedByTheWorkersLongestService()
    {
        // The seed calendar has a ten-minute buffer and its worker offers one 45-minute service.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(12, 0), DayOfWeek.Monday);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 0);

        var slots = await ReadSlotsAsync(seed);

        // 45 + 10 = 55 minutes of stride; three whole slots fit in three hours (the fourth would end
        // at 12:10). Moscow is UTC+3 and does not observe DST, so 09:00 local is 06:00Z all year -
        // which is exactly why the DST tests below use a different zone.
        Assert.Equal(3, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 5, 4, 6, 0, 0, TimeSpan.Zero), slots[0].StartsAt);
        Assert.Equal(TimeSpan.FromMinutes(45), slots[0].EndsAt - slots[0].StartsAt);
        Assert.Equal(TimeSpan.FromMinutes(55), slots[1].StartsAt - slots[0].StartsAt);
        Assert.All(slots, slot => Assert.Equal(new DateOnly(2026, 5, 4), slot.LocalDate));
    }

    [Fact]
    public async Task ARuleAtNineLocal_IsNineLocalOnBothSidesOfASpringForward()
    {
        // THE test this domain exists for. America/New_York, not Europe/Moscow: Russia has not
        // observed DST since 2014, so a Moscow-only test passes against code that stored a fixed
        // offset and proves nothing. US DST began 2026-03-08.
        var seed = await CalendarSeed.WriteAsync(fixture, "America/New_York");
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(10, 0), DayOfWeek.Saturday);

        // 2026-03-07 is the Saturday before the transition, 2026-03-14 the Saturday after.
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 8);

        var slots = await ReadSlotsAsync(seed);
        Assert.Equal(2, slots.Count);

        // Same wall clock, different instants - 14:00Z on standard time, 13:00Z on daylight time.
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), slots[0].StartsAt.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 3, 14, 13, 0, 0, TimeSpan.Zero), slots[1].StartsAt.ToUniversalTime());

        // Seven days apart on the wall, six days and twenty-three hours apart in real time. Code
        // that had baked an offset into the rule would have produced exactly seven days here and
        // opened the shop an hour late for every day after the transition, with nothing looking
        // wrong anywhere.
        Assert.Equal(
            TimeSpan.FromDays(7) - TimeSpan.FromHours(1),
            slots[1].StartsAt - slots[0].StartsAt);

        // And the local day is the business's own day, on both sides.
        Assert.Equal([new DateOnly(2026, 3, 7), new DateOnly(2026, 3, 14)], slots.Select(s => s.LocalDate));
    }

    [Fact]
    public async Task TheDayTheClocksSpringForward_IsAnHourShorter_AndNoSlotStartsInsideTheGap()
    {
        // A rule that brackets the transition: 01:00-06:00 on 2026-03-08, when 02:00 becomes 03:00.
        // Five hours on the wall clock, four hours of real time.
        var seed = await CalendarSeed.WriteAsync(fixture, "America/New_York");
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(1, 0), new TimeOnly(6, 0), DayOfWeek.Sunday);

        // 05:00Z is midnight local on the 8th - inside the day under test and before its first slot.
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 0);

        var slots = await ReadSlotsAsync(seed);

        // 01:00 EST is 06:00Z; 06:00 EDT is 10:00Z. Four real hours from a five-hour wall-clock
        // window, so 45 + 10 = 55 minutes of stride yields four slots and not the five a wall-clock
        // subtraction would have produced. Nothing in the materialiser knows why - it divided a span
        // of absolute time, which is the entire benefit of converting once at the edge.
        Assert.Equal(4, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), slots[0].StartsAt.ToUniversalTime());
        Assert.True(slots[^1].EndsAt <= new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero));

        // 02:00-03:00 local never happened on this date, and no slot claims it did: the second slot
        // starts at 06:55Z, which renders as 01:55 EST, and the third at 07:50Z, which renders as
        // 03:50 EDT. The clocks moved between them and no slot boundary landed in the hole.
        Assert.All(slots, slot => Assert.NotEqual(2, LocalHourOf(slot.StartsAt)));

        // None of them overlaps - which Postgres has already agreed with, since the exclusion
        // constraint accepted all four.
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.False(slots[i - 1].Slot.Overlaps(slots[i].Slot));
        }
    }

    [Fact]
    public async Task TheDayTheClocksFallBack_IsAnHourLonger()
    {
        // 2026-11-01: 02:00 becomes 01:00, so the wall-clock hour 01:00-02:00 happens twice. A
        // 00:00-06:00 rule is six hours on the wall and seven in real time, and the worker really is
        // at work for both passes - so the day must produce more slots, not fewer.
        var seed = await CalendarSeed.WriteAsync(fixture, "America/New_York");
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(0, 0), new TimeOnly(6, 0), DayOfWeek.Sunday);

        // 04:00Z is midnight local on the 1st (still on daylight time).
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 0);

        var slots = await ReadSlotsAsync(seed);

        // 00:00 EDT is 04:00Z; 06:00 EST is 11:00Z. Seven real hours from a six-hour wall-clock
        // window, so seven slots of 55-minute stride instead of six. A shorter day here would have
        // been an hour of the worker's real availability silently deleted.
        Assert.Equal(7, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), slots[0].StartsAt.ToUniversalTime());
        Assert.True(slots[^1].EndsAt <= new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero));

        // Two different slots start at the same wall-clock hour, one on each pass of the repeated
        // hour, and Postgres accepted both - the exclusion constraint compares instants, which is
        // the reason `events` stores timestamptz rather than a local time plus a date.
        Assert.All(slots, slot => Assert.Equal(new DateOnly(2026, 11, 1), slot.LocalDate));
    }

    [Fact]
    public async Task AWorkerWhoPerformsNoService_GetsNoSlots()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var bare = Worker.Create(new WorkerId(CalendarSeed.NewId()), seed.Tenant.Id, "Bare");
        bare.JoinCalendar(seed.Calendar);

        await using (var db = fixture.CreateDbContext())
        {
            db.Workers.Add(bare);
            db.WorkingHoursRules.Add(WorkingHoursRule.For(
                new WorkingHoursRuleId(CalendarSeed.NewId()), bare, seed.Calendar,
                DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
            await db.SaveChangesAsync();
        }

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 0);

        // Nothing, rather than a guessed slot length. A slot exists before its service is chosen, so
        // its length has to fit every service the worker performs - and there is no such length for
        // a worker who performs none.
        await using var reader = fixture.CreateDbContext();
        Assert.Equal(0, await reader.Events.CountAsync(e => e.WorkerId == bare.Id));
    }

    [Fact]
    public async Task SlotsThatHaveAlreadyEnded_AreNeverPublished()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday);

        // 14:00 Moscow on the Monday: the morning is over.
        var harness = new AvailabilityHarness(
            fixture, new FixedClock(new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id, horizonDays: 0);

        var slots = await ReadSlotsAsync(seed);

        // A slot in the past is not merely useless - Event.Claim refuses it, so it would be a row a
        // customer can see and cannot book, which is worse than an absence.
        Assert.NotEmpty(slots);
        Assert.All(slots, slot =>
            Assert.True(slot.EndsAt > new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero)));
    }

    private static int LocalHourOf(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Hour;

    /// <summary><see cref="Event"/> is an aggregate, so it has reference equality - comparing two
    /// reads of the table needs the values that would differ if a row had been rewritten or
    /// duplicated.</summary>
    private static List<(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, EventStatus Status)> Fingerprint(
        IEnumerable<Event> slots) =>
        slots.Select(e => (e.Id.Value, e.StartsAt, e.EndsAt, e.Status)).ToList();

    private async Task<List<Event>> ReadSlotsAsync(SeededTenant seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Events
            .Where(e => e.CalendarId == seed.Calendar.Id && e.WorkerId == seed.Worker.Id)
            .OrderBy(e => e.StartsAt)
            .ToListAsync();
    }
}
