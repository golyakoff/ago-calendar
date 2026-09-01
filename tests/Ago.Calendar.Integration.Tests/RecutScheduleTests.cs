using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-16`: the one deliberate, human-triggered exception to `20-14`'s forward-only cursor. Against a
/// real Postgres, for the identical reason <see cref="ManualDayEditingTests"/> is - the guarantees here
/// (a kept booking's day is left byte-for-byte alone, a cancelled booking's row survives, the
/// exclusion constraint is what actually refuses a re-cut around a claimed slot) are claims about what
/// the database does with a statement, not about what an in-memory fake agrees with itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RecutScheduleTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Monday = new(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Tuesday = new(2026, 5, 5);
    private const int HorizonDays = 10;

    [Fact]
    public async Task NoBookingsOnADay_IsClearedAndRegeneratedFromTheCurrentTemplate()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        // The tenant fixes their schedule after the horizon was already cut - the whole reason this
        // item exists. 45+10=55-minute slots become 30-minute, no-buffer ones.
        await ReconfigureAsync(seed, slotMinutes: 30, bufferMinutes: 0);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var confirm = await harness.RecutConfirmAsync(seed, Tuesday, preview.Value.Fingerprint);
        Assert.True(confirm.IsSuccess, confirm.Error?.Message);

        // Every day from Tuesday (today + 1) to today + HorizonDays was empty of bookings, so every
        // one of them was cleared and re-cut - none skipped. That is HorizonDays days: Tuesday
        // through Thursday the 14th inclusive.
        Assert.Equal(HorizonDays, confirm.Value.RecutDays.Count);
        Assert.Empty(confirm.Value.SkippedDays);

        var day = await ReadDayAsync(seed, Tuesday);
        // 09:00-18:00 is nine hours; at 30 minutes with no buffer that is exactly 18 slots - proof
        // the *new* template produced this day, not a copy of the old 55-minute-stride grid.
        Assert.Equal(18, day.Count);
        Assert.All(day, e => Assert.Equal(EventStatus.Available, e.Status));
        Assert.Equal(TimeSpan.FromMinutes(30), day[1].StartsAt - day[0].StartsAt);
    }

    [Fact]
    public async Task ADayWithAKeptBooking_IsLeftEntirelyInTheOldGrid_AndReportedAsSkipped()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        var beforeDay = await ReadDayAsync(seed, Tuesday);
        var booking = await ClaimOneSlotOnAsync(seed, Tuesday);

        await ReconfigureAsync(seed, slotMinutes: 30, bufferMinutes: 0);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        var tuesdayPreview = preview.Value.Days.Single(d => d.LocalDate == Tuesday);
        var bookingPreview = Assert.Single(tuesdayPreview.Bookings);
        Assert.Equal(booking.Id, bookingPreview.BookingId);
        Assert.True(bookingPreview.CanDecide);

        var confirm = await harness.RecutConfirmAsync(
            seed, Tuesday, preview.Value.Fingerprint, new RecutBookingDecision(booking.Id, RecutDecision.Keep));
        Assert.True(confirm.IsSuccess, confirm.Error?.Message);

        Assert.Contains(Tuesday, confirm.Value.SkippedDays);
        Assert.DoesNotContain(Tuesday, confirm.Value.RecutDays);

        // Byte-for-byte the old grid: same row count, same ids, same statuses, same slot length -
        // nothing about this day changed at all, even though every other day in range did.
        var afterDay = await ReadDayAsync(seed, Tuesday);
        Assert.Equal(beforeDay.Count, afterDay.Count);
        Assert.Equal(beforeDay.Select(e => e.Id).OrderBy(id => id.Value), afterDay.Select(e => e.Id).OrderBy(id => id.Value));
        Assert.Contains(afterDay, e => e.Id == booking.Id && e.Status == EventStatus.PendingConfirmation);

        // A day further out with nothing on it was still cleared and re-cut under the new template -
        // the kept booking protects only its own day.
        var wednesday = await ReadDayAsync(seed, Tuesday.AddDays(1));
        Assert.All(wednesday, e => Assert.Equal(EventStatus.Available, e.Status));
        Assert.Equal(TimeSpan.FromMinutes(30), wednesday[1].StartsAt - wednesday[0].StartsAt);
    }

    [Fact]
    public async Task ACancelledBooking_GoesThroughTheOrdinaryCancellationPath_AndItsRowSurvives()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        var booking = await ClaimOneSlotOnAsync(seed, Tuesday);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var confirm = await harness.RecutConfirmAsync(
            seed, Tuesday, preview.Value.Fingerprint, new RecutBookingDecision(booking.Id, RecutDecision.Cancel));
        Assert.True(confirm.IsSuccess, confirm.Error?.Message);

        Assert.Equal(1, confirm.Value.BookingsCancelled);
        Assert.Contains(Tuesday, confirm.Value.RecutDays);

        var afterDay = await ReadDayAsync(seed, Tuesday);
        // Not deleted: the exact same row id survives, now Cancelled - never Available, never gone.
        var cancelled = Assert.Single(afterDay, e => e.Id == booking.Id);
        Assert.Equal(EventStatus.Cancelled, cancelled.Status);

        // The day itself was still cleared and re-cut around the now-cancelled row: fresh Available
        // slots exist alongside it (adr/0053's own filter keeps a Cancelled row out of the exclusion
        // constraint, so the new grid and the cancelled row coexist without a gap).
        Assert.Contains(afterDay, e => e.Status == EventStatus.Available);
    }

    [Fact]
    public async Task ABookingCreatedBetweenPreviewAndConfirm_RefusesTheWholeOperation()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.All(preview.Value.Days, d => Assert.Empty(d.Bookings));

        // The public widget never stops taking bookings, including in the gap between an operator
        // reading a preview and pressing confirm.
        var lateBooking = await ClaimOneSlotOnAsync(seed, Tuesday);

        var confirm = await harness.RecutConfirmAsync(seed, Tuesday, preview.Value.Fingerprint);

        Assert.True(confirm.IsFailure);
        Assert.Equal("recut.stale", confirm.Error!.Value.Code);

        // Refused whole, not partially applied: the late booking is exactly as the customer left it,
        // and nothing on the day was cleared or re-cut.
        var day = await ReadDayAsync(seed, Tuesday);
        Assert.Contains(day, e => e.Id == lateBooking.Id && e.Status == EventStatus.PendingConfirmation);
        Assert.DoesNotContain(day, e => e.Status == EventStatus.Cancelled);
    }

    [Fact]
    public async Task ADecidableBookingWithNoDecision_RefusesTheWholeOperation()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        var booking = await ClaimOneSlotOnAsync(seed, Tuesday);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        // No decision supplied at all for the one booking in range.
        var confirm = await harness.RecutConfirmAsync(seed, Tuesday, preview.Value.Fingerprint);

        Assert.True(confirm.IsFailure);
        Assert.Equal("recut.missing_decision", confirm.Error!.Value.Code);

        var day = await ReadDayAsync(seed, Tuesday);
        Assert.Contains(day, e => e.Id == booking.Id && e.Status == EventStatus.PendingConfirmation);
    }

    [Fact]
    public async Task FromAtOrPastTheCurrentCursor_IsRefused_AsNotARegression()
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        // The cursor after materialisation sits past the whole horizon - asking to "re-cut" from
        // there is an ordinary forward save with nothing already cut to undo.
        var preview = await harness.RecutPreviewAsync(seed, Monday.AddDays(HorizonDays + 30).DateOnly());

        Assert.True(preview.IsFailure);
        Assert.Equal("recut.not_a_regression", preview.Error!.Value.Code);
    }

    [Fact]
    public async Task AnotherWorkerOnTheSameCalendar_AndTheDayBeforeFrom_AreBothLeftUntouched()
    {
        var (seed, harness) = await AMaterializedWeekAsync();
        var otherWorker = await ASecondWorkerOnTheSameCalendarAsync(seed);

        var mondayBefore = await ReadDayAsync(seed, DateOnly.FromDateTime(Monday.UtcDateTime));
        var otherWorkerTuesdayBefore = await ReadDayForWorkerAsync(seed, otherWorker.Id, Tuesday);

        await ReconfigureAsync(seed, slotMinutes: 30, bufferMinutes: 0);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        var confirm = await harness.RecutConfirmAsync(seed, Tuesday, preview.Value.Fingerprint);
        Assert.True(confirm.IsSuccess, confirm.Error?.Message);

        // Out of range for the recut worker: unchanged.
        var mondayAfter = await ReadDayAsync(seed, DateOnly.FromDateTime(Monday.UtcDateTime));
        Assert.Equal(
            mondayBefore.Select(e => (e.Id, e.StartsAt, e.EndsAt)),
            mondayAfter.Select(e => (e.Id, e.StartsAt, e.EndsAt)));

        // A different worker entirely, same calendar, same local date: untouched by a recut scoped to
        // one worker - "one worker, one operation" is not merely a console-side restriction.
        var otherWorkerTuesdayAfter = await ReadDayForWorkerAsync(seed, otherWorker.Id, Tuesday);
        Assert.Equal(
            otherWorkerTuesdayBefore.Select(e => (e.Id, e.StartsAt, e.EndsAt)),
            otherWorkerTuesdayAfter.Select(e => (e.Id, e.StartsAt, e.EndsAt)));
    }

    [Fact]
    public async Task ADayWithANoShow_IsSkippedWithNoDecisionOffered_EvenThoughItCannotBeCancelled()
    {
        var (seed, harness) = await ANoShowOnAsync(Tuesday);
        var beforeDay = await ReadDayAsync(seed, Tuesday);

        var preview = await harness.RecutPreviewAsync(seed, Tuesday);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        var tuesdayPreview = preview.Value.Days.Single(d => d.LocalDate == Tuesday);
        var bookingPreview = Assert.Single(tuesdayPreview.Bookings);
        Assert.Equal(EventStatus.NoShow, bookingPreview.Status);
        // No control offered - Event.Cancel refuses a NoShow row, so there is no decision to make.
        Assert.False(bookingPreview.CanDecide);

        // No decision supplied at all - the confirm succeeds anyway, because a NoShow never needs one.
        var confirm = await harness.RecutConfirmAsync(seed, Tuesday, preview.Value.Fingerprint);
        Assert.True(confirm.IsSuccess, confirm.Error?.Message);

        Assert.Contains(Tuesday, confirm.Value.SkippedDays);
        Assert.DoesNotContain(Tuesday, confirm.Value.RecutDays);

        var afterDay = await ReadDayAsync(seed, Tuesday);
        Assert.Equal(
            beforeDay.Select(e => (e.Id, e.Status)).OrderBy(e => e.Id.Value),
            afterDay.Select(e => (e.Id, e.Status)).OrderBy(e => e.Id.Value));
    }

    /// <summary>A booked slot whose visit already happened and was recorded as a no-show, built through
    /// the real state machine - <c>Claim -&gt; Confirm -&gt; MarkNoShow</c> - each with whatever instant
    /// that transition's own precondition needs, independent of the harness's own fixed clock.</summary>
    private async Task<(SeededTenant Seed, AvailabilityHarness Harness)> ANoShowOnAsync(DateOnly localDate)
    {
        var (seed, harness) = await AMaterializedWeekAsync();

        await using var db = fixture.CreateDbContext();
        var target = await db.Events
            .Where(e => e.WorkerId == seed.Worker.Id && e.LocalDate == localDate && e.Status == EventStatus.Available)
            .OrderBy(e => e.StartsAt)
            .FirstAsync();

        var claimedAt = target.StartsAt - TimeSpan.FromHours(1);
        target.Claim(seed.Customer.Id, seed.Service.Id, claimedAt, claimedAt.AddMinutes(15));
        target.ClearDomainEvents();
        target.Confirm(claimedAt.AddMinutes(5));
        target.ClearDomainEvents();
        target.MarkNoShow(target.EndsAt.AddMinutes(1));
        target.ClearDomainEvents();
        await new EventRepository(db).SaveAsync(target, CancellationToken.None);

        return (seed, harness);
    }

    private async Task<(SeededTenant Seed, AvailabilityHarness Harness)> AMaterializedWeekAsync()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: HorizonDays);

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);
        return (seed, harness);
    }

    /// <summary>Load-mutate-save through the real aggregate method a console save would use - the
    /// cursor stays exactly where the job left it (forward-only, unaffected), only the template
    /// numbers change.</summary>
    private async Task ReconfigureAsync(SeededTenant seed, int slotMinutes, int bufferMinutes)
    {
        await using var db = fixture.CreateDbContext();
        var schedule = await db.WorkerSchedules.SingleAsync(s => s.WorkerId == seed.Worker.Id);
        schedule.ReconfigureWeekly(slotMinutes, bufferMinutes, schedule.HorizonDays, schedule.MaterializeFrom, Monday);
        await new WorkerScheduleRepository(db).SaveAsync(schedule, CancellationToken.None);
    }

    /// <summary>Constructs "this day already has a booking" through the real state machine - see
    /// <c>ManualDayEditingTests.ClaimOneSlotOnAsync</c> for the identical reasoning.</summary>
    private async Task<Event> ClaimOneSlotOnAsync(SeededTenant seed, DateOnly localDate)
    {
        await using var db = fixture.CreateDbContext();
        var target = await db.Events
            .Where(e => e.WorkerId == seed.Worker.Id && e.LocalDate == localDate && e.Status == EventStatus.Available)
            .OrderBy(e => e.StartsAt)
            .FirstAsync();

        target.Claim(seed.Customer.Id, seed.Service.Id, Monday, Monday.AddMinutes(15));
        target.ClearDomainEvents();
        await new EventRepository(db).SaveAsync(target, CancellationToken.None);
        return target;
    }

    private async Task<Worker> ASecondWorkerOnTheSameCalendarAsync(SeededTenant seed)
    {
        await using var db = fixture.CreateDbContext();
        var calendar = await db.Calendars.SingleAsync(c => c.Id == seed.Calendar.Id);

        var worker = Worker.Create(new WorkerId(CalendarSeed.NewId()), seed.Tenant.Id, "Roe", "Jamie", null, Monday);
        worker.JoinCalendar(calendar);

        var schedule = WorkerSchedule.CreateWeekly(
            new WorkerScheduleId(CalendarSeed.NewId()), worker.Id,
            slotMinutes: 45, bufferMinutes: 10, horizonDays: HorizonDays, materializeFrom: DateOnly.MinValue, Monday);

        db.Workers.Add(worker);
        db.WorkerSchedules.Add(schedule);
        db.WorkingHoursRules.AddRange(CalendarSeed.EveryDay.Select(day =>
            WorkingHoursRule.For(
                new WorkingHoursRuleId(CalendarSeed.NewId()), worker, calendar, day,
                new TimeOnly(9, 0), new TimeOnly(18, 0))));
        await db.SaveChangesAsync();

        var harness = new AvailabilityHarness(fixture, new FixedClock(Monday));
        await harness.MaterializeAsync(seed.Calendar.Id);

        return worker;
    }

    private async Task<List<Event>> ReadDayAsync(SeededTenant seed, DateOnly localDate) =>
        await ReadDayForWorkerAsync(seed, seed.Worker.Id, localDate);

    private async Task<List<Event>> ReadDayForWorkerAsync(SeededTenant seed, WorkerId workerId, DateOnly localDate)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Events
            .Where(e => e.WorkerId == workerId && e.LocalDate == localDate)
            .OrderBy(e => e.StartsAt)
            .ToListAsync();
    }
}

file static class DateTimeOffsetExtensions
{
    public static DateOnly DateOnly(this DateTimeOffset value) => System.DateOnly.FromDateTime(value.UtcDateTime);
}
