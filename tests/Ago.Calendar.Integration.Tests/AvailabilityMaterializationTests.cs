using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-02`'s central claims, against a real Postgres with the real exclusion constraint in place:
/// the run is idempotent, it never touches a row that has moved past <c>Available</c>, and the one
/// wall-clock-to-instant conversion behaves correctly on both sides of a DST transition.
///
/// <para><b>`20-14`: every test here now seeds a <see cref="WorkerSchedule"/> alongside the worker's
/// working hours.</b> Slot length, buffer and horizon used to come from the calendar and the worker's
/// longest offered service; they now come from the worker's own schedule, and a worker with no
/// schedule at all materialises nothing regardless of what hours or services he has. Every schedule
/// below is seeded with <c>materializeFrom: DateOnly.MinValue</c> (the default
/// <see cref="CalendarSeed.AddWeeklyScheduleAsync"/> uses) so <c>firstDay</c> always resolves to
/// "today" - the same starting point every one of these tests had before this item, when there was no
/// cursor to consider at all.</para>
///
/// <para><b>One consequence worth stating once, here, rather than at every call site it touches.</b>
/// The cursor advances past the whole horizon window on its very first run, so a *second* call in the
/// same test - same fixed clock, nothing else changed - finds nothing left to consider at all
/// (<c>firstDay > lastDay</c>) and returns without touching the database. That is a stronger, cheaper
/// version of "safe to repeat" than the row-by-row skip this suite tested before this item, and it is
/// why several assertions below now check <c>DaysConsidered == 0</c> on a rerun rather than
/// <c>DaysConsidered == DaysSkipped</c> as they did previously - see
/// <c>MaterializeAvailabilityHandler</c>'s own remarks for the full reasoning and the trade this
/// makes.</para>
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
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 6);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));

        var first = await harness.MaterializeAsync(seed.Calendar.Id);
        var afterFirstRun = Fingerprint(await ReadSlotsAsync(seed));

        var second = await harness.MaterializeAsync(seed.Calendar.Id);
        var afterSecondRun = Fingerprint(await ReadSlotsAsync(seed));

        Assert.True(first.SlotsInserted > 0, "the first run must actually generate something");

        // The assertion the item is named for. Not "no duplicates were visible" but "the second run
        // wrote zero rows" - a stronger statement, and one that stays true even if a future
        // duplicate happened to be invisible to the query above.
        Assert.Equal(0, second.SlotsInserted);

        // `20-14`: the cursor already advanced past the whole horizon on the first run, so the second
        // call finds nothing left in [max(today, cursor), today + horizon] to even consider.
        Assert.Equal(0, second.DaysConsidered);

        // Same rows, same ids, same instants: not merely the same count.
        Assert.Equal(afterFirstRun, afterSecondRun);
    }

    [Fact]
    public async Task AClaimedSlot_SurvivesARepeatedRun_AndIsNotDuplicated()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 6);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
        // daily for a month would reach this row thirty times. `20-14`'s own cursor makes both of
        // these calls a fast no-op (see the class remarks), which is itself part of what "safe to
        // repeat" now means.
        await harness.MaterializeAsync(seed.Calendar.Id);
        var rerun = await harness.MaterializeAsync(seed.Calendar.Id);

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
    public async Task SlotsAreSpacedByTheWorkersSchedule_AndSizedByItsOwnSlotLength()
    {
        // `20-14`: ten-minute buffer, 45-minute slot - the worker's own schedule now, not the
        // calendar's buffer and the longest offered service.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(12, 0), DayOfWeek.Monday);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 0);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
    public async Task TwoWorkersOnOneCalendar_WithDifferentSlotLengths_ProduceDifferentGrids()
    {
        // `20-14`'s own Done-when: two workers, two schedules, two grids on the same day - proof
        // that slot length is no longer one number derived per calendar.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(12, 0), DayOfWeek.Monday);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 0, slotMinutes: 45, bufferMinutes: 0);

        var second = Domain.Worker.Create(
            new WorkerId(CalendarSeed.NewId()), seed.Tenant.Id, "Roe", "Bo", null, CalendarSeed.Now);
        second.JoinCalendar(seed.Calendar);
        second.Offer(seed.Service);

        await using (var db = fixture.CreateDbContext())
        {
            db.Workers.Add(second);
            db.WorkingHoursRules.Add(WorkingHoursRule.For(
                new WorkingHoursRuleId(CalendarSeed.NewId()), second, seed.Calendar,
                DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)));
            db.WorkerSchedules.Add(WorkerSchedule.CreateWeekly(
                new WorkerScheduleId(CalendarSeed.NewId()), second.Id,
                slotMinutes: 60, bufferMinutes: 0, horizonDays: 0, DateOnly.MinValue, CalendarSeed.Now));
            await db.SaveChangesAsync();
        }

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

        await using var reader = fixture.CreateDbContext();
        var firstSlots = await reader.Events
            .Where(e => e.WorkerId == seed.Worker.Id).OrderBy(e => e.StartsAt).ToListAsync();
        var secondSlots = await reader.Events
            .Where(e => e.WorkerId == second.Id).OrderBy(e => e.StartsAt).ToListAsync();

        // Zero buffer for both, so each grid divides the three-hour window evenly but into a
        // different number of slots: four 45-minute slots back to back for the first worker, three
        // 60-minute slots for the second - the same window cut two different ways by two different
        // schedules.
        Assert.Equal(4, firstSlots.Count);
        Assert.All(firstSlots, e => Assert.Equal(TimeSpan.FromMinutes(45), e.EndsAt - e.StartsAt));

        Assert.Equal(3, secondSlots.Count);
        Assert.All(secondSlots, e => Assert.Equal(TimeSpan.FromMinutes(60), e.EndsAt - e.StartsAt));
    }

    [Fact]
    public async Task ACycleScheduleAnchoredOnAMonday_TwoOnTwoOff_ProducesSlotsOnExactlyTheRightDaysAcrossAMonth()
    {
        // `20-14`'s own Done-when, at the level that actually matters: not just CycleGrid's pure
        // arithmetic (Ago.Calendar.Domain.Tests.CycleGridTests already proves that with no database),
        // but the full materialisation pipeline producing real rows only on the days it should.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var anchor = new DateOnly(2026, 5, 4); // the same Monday every other test in this file uses.
        await CalendarSeed.AddCycleScheduleAsync(
            fixture, seed, anchor, workingDays: 2, restDays: 2,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0), horizonDays: 27);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

        var slots = await ReadSlotsAsync(seed);
        var workingDates = slots.Select(s => s.LocalDate).Distinct().ToHashSet();

        // Every day across the horizon agrees with CycleGrid's own answer - the anchor day itself
        // (Monday, working) and a resting day (Wednesday) both checked explicitly, then every day in
        // the window checked against the same predicate this handler is supposed to be using.
        Assert.Contains(anchor, workingDates);
        Assert.DoesNotContain(anchor.AddDays(2), workingDates);

        for (var day = anchor; day <= anchor.AddDays(27); day = day.AddDays(1))
        {
            Assert.Equal(CycleGrid.IsWorkingDay(anchor, 2, 2, day), workingDates.Contains(day));
        }
    }

    [Fact]
    public async Task ACycleScheduleAnchoredOnAMonday_OneOnThreeOff_ProducesSlotsOnOneDayInFour_InsideStatedHoursOnly()
    {
        // "Сутки через трое" - the author's own second named case, and the test that proves the
        // midnight problem really did dissolve: the worker's hours are an ordinary 09:00-21:00
        // daytime window on his one working day in four, never a 24-hour span.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var anchor = new DateOnly(2026, 5, 4);
        await CalendarSeed.AddCycleScheduleAsync(
            fixture, seed, anchor, workingDays: 1, restDays: 3,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(21, 0), horizonDays: 27, bufferMinutes: 0);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

        var slots = await ReadSlotsAsync(seed);
        var workingDates = slots.Select(s => s.LocalDate).Distinct().ToHashSet();

        // 28 days in the window (anchor plus 27 more), one working day in every four = 7.
        Assert.Equal(7, workingDates.Count);
        Assert.All(workingDates, day => Assert.True(CycleGrid.IsWorkingDay(anchor, 1, 3, day)));

        // No slot crosses midnight: every slot's start and end fall on the same business-local day as
        // the day it was generated for - the structural guarantee of resolving one same-day window
        // per working day, never a span that could straddle two.
        Assert.All(slots, slot =>
            Assert.Equal(slot.LocalDate, DateOnly.FromDateTime(slot.StartsAt.UtcDateTime.AddHours(3))));
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
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 8);

        // 2026-03-07 is the Saturday before the transition, 2026-03-14 the Saturday after.
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 0);

        // 05:00Z is midnight local on the 8th - inside the day under test and before its first slot.
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 0);

        // 04:00Z is midnight local on the 1st (still on daylight time).
        var harness = new AvailabilityHarness(fixture, new FixedClock(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
    public async Task AWorkerWithNoSchedule_GetsNoSlots()
    {
        // `20-14`'s own open question, decided: a schedule is written by a human, never conjured as a
        // default. A worker with working hours and even a service offered, but no schedule of his
        // own, is exactly as unbookable as the pre-`20-14` "performs no service" case was - the
        // handler has nothing to derive a slot length or a horizon from either way.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var bare = Worker.Create(
            new WorkerId(CalendarSeed.NewId()), seed.Tenant.Id, "Bare", "Bare", null, CalendarSeed.Now);
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
        await harness.MaterializeAsync(seed.Calendar.Id);

        // Nothing, rather than a guessed slot length. A slot exists before its service is chosen, so
        // its length has to come from somewhere the worker actually controls - and a worker with no
        // schedule has stated nothing.
        await using var reader = fixture.CreateDbContext();
        Assert.Equal(0, await reader.Events.CountAsync(e => e.WorkerId == bare.Id));
    }

    [Fact]
    public async Task SlotsThatHaveAlreadyEnded_AreNeverPublished()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 0);

        // 14:00 Moscow on the Monday: the morning is over.
        var harness = new AvailabilityHarness(
            fixture, new FixedClock(new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero)));
        await harness.MaterializeAsync(seed.Calendar.Id);

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
