using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.MaterializeAvailability;

/// <summary>
/// Generates the <see cref="EventStatus.Available"/> rows a customer will later claim, out to each
/// worker's own rolling horizon, for every active worker on one calendar.
///
/// <para><b>Why rows exist before anybody books them.</b> The rejected alternative is to compute
/// availability on the fly - take the worker's rules, subtract the bookings, hand the customer the
/// gaps. It loses on the one question this product must answer correctly: *is this slot still
/// free?* A computed gap is not a thing, so "take it" cannot be a compare-and-set against anything;
/// it needs a lock invented over an interval, and two customers who both computed the same gap both
/// pass every check an application layer can make. A materialised row turns the whole race into
/// <c>UPDATE ... WHERE id = @id AND status = 'Available'</c>, whose rows-affected count is the
/// verdict and whose arbiter is Postgres.</para>
///
/// <para><b>`20-14`: what feeds the grid moved from the calendar to the worker.</b> Slot length used
/// to be the longest service a worker offers, the buffer used to be per calendar, and the horizon used
/// to be one deployment-wide number. All three now come from <see cref="WorkerSchedule"/>, which also
/// decides *which days* are working ones: a <see cref="ScheduleKind.Weekly"/> schedule still reads
/// <see cref="WorkingHoursRule"/> exactly as `20-02` always did, and a <see cref="ScheduleKind.Cycle"/>
/// schedule asks <see cref="WorkerSchedule.IsCycleWorkingDay"/> instead - the same
/// <c>GenerateDay</c> call either way, branched once on <see cref="WorkerSchedule.Kind"/>. A worker
/// with no schedule at all has nothing bookable, the same conclusion this handler already drew for a
/// worker who offers no service, before this item.</para>
///
/// <para><b>The non-destructive rule, stated once and enforced twice.</b> <i>This handler only ever
/// inserts, and only into business-local days that have no event row at all.</i> It never updates,
/// never deletes, and never regenerates a day it has already generated - so a slot that has moved on
/// to <c>PendingConfirmation</c>, <c>Booked</c>, <c>Cancelled</c> or <c>NoShow</c>, and a day a
/// tenant has edited by hand, are both untouchable by construction rather than by a check somebody
/// has to remember. The day-set query below is the cheap half of that; the exclusion constraint on
/// <c>events</c> is the half that holds when two <c>Ago.Calendar.Worker</c> replicas run this at the
/// same instant and both see the same empty day (adr/0049, adr/0053).</para>
///
/// <para><b>The cursor is the other half of "safe to repeat", and it is cheaper than the row check.</b>
/// <see cref="WorkerSchedule.MaterializeFrom"/> only ever advances, to one past the last day this run
/// covered - so a second run with the same wall clock has nothing left in
/// <c>[max(today, MaterializeFrom), today + HorizonDays]</c> to consider at all and returns without
/// touching the database a second time. In real, daily operation the window this collapses to is a
/// single new day: the day the rolling horizon just grew into. One consequence worth stating plainly,
/// because it is easy to miss: a <see cref="WorkingHoursRule"/> added for a day that already fell
/// inside a previously-cut window is not retroactively backfilled once the cursor has passed it - the
/// job only ever looks forward from the cursor, never back below it. `20-14`'s own "Decided" section
/// states the cursor rule in exactly these terms; the trade is deliberate; re-cutting a day the cursor
/// has already passed is `20-16`'s job.</para>
///
/// <para><b>Where wall clock becomes an instant.</b> Exactly one call, on the line that resolves a
/// day's working hours - a <see cref="WorkingHoursRule"/> or the schedule's own cycle hours - against
/// <see cref="BookingCalendar.TimeZone"/>, plus the one that asks which local day "now" falls on.
/// Everything after that - <see cref="SlotGrid"/>, the buffer arithmetic, the "has this slot already
/// ended" filter, the constraint - is absolute time.</para>
/// </summary>
public sealed class MaterializeAvailabilityHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkingHoursRuleRepository rules,
    IWorkerScheduleRepository schedules,
    IEventRepository events,
    IWallClockResolver wallClock,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<AvailabilityMaterialized> HandleAsync(
        MaterializeAvailability command, CancellationToken cancellationToken)
    {
        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        if (calendar is null)
        {
            // A calendar deleted between the job listing it and this handler loading it. Not an
            // error: the job's next tick simply will not list it.
            return AvailabilityMaterialized.Nothing;
        }

        var now = clock.UtcNow;

        // "Today" is a question about a place, not about UTC - 21:00 in New York is already tomorrow
        // in UTC, so a horizon counted from the UTC date would be a day out for every calendar west
        // of Greenwich for part of every day.
        var today = wallClock.ToLocalDate(calendar.TimeZone, now);

        var calendarWorkers = await workers.ListActiveForCalendarAsync(calendar.Id, cancellationToken);
        if (calendarWorkers.Count == 0)
        {
            return AvailabilityMaterialized.Nothing;
        }

        var allRules = await rules.ListForCalendarAsync(calendar.Id, cancellationToken);
        var schedulesByWorker = (await schedules.ListForCalendarAsync(calendar.Id, cancellationToken))
            .ToDictionary(schedule => schedule.WorkerId);

        var daysConsidered = 0;
        var daysSkipped = 0;
        var slotsInserted = 0;

        foreach (var worker in calendarWorkers)
        {
            if (!schedulesByWorker.TryGetValue(worker.Id, out var schedule))
            {
                // A worker with no schedule yet has nothing bookable - `20-14`'s open question,
                // decided: a schedule is written by a human, never conjured as a default. Silently
                // producing slots of a guessed length would be worse than producing none.
                continue;
            }

            var firstDay = today > schedule.MaterializeFrom ? today : schedule.MaterializeFrom;
            var lastDay = today.AddDays(schedule.HorizonDays);
            if (lastDay < firstDay)
            {
                // The cursor already reaches past this run's horizon - e.g. a tenant paused the
                // schedule into the future. Nothing to do, and nothing to advance: there is no "past
                // what it cut" when nothing was cut.
                continue;
            }

            var workerRules = schedule.Kind == ScheduleKind.Weekly
                ? allRules.Where(rule => rule.WorkerId == worker.Id).ToList()
                : [];

            var alreadyMaterialized = await events.ListMaterializedLocalDatesAsync(
                calendar.Id, worker.Id, firstDay, lastDay, cancellationToken);

            var generated = new List<Event>();
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                daysConsidered++;

                // THE non-destructive rule, in one line: a day that already has any event row -
                // available, booked, blocked by a tenant's day-off, or cancelled - is never
                // regenerated. Everything this item promises about bookings surviving a repeated run
                // and manual edits surviving the next job tick reduces to this predicate.
                if (alreadyMaterialized.Contains(day))
                {
                    daysSkipped++;
                    continue;
                }

                generated.AddRange(GenerateDay(calendar, worker, schedule, day, workerRules, now));
            }

            slotsInserted += await events.InsertAvailableSlotsAsync(generated, cancellationToken);

            // Past what this run cut, always - whether or not it generated anything: a Weekly
            // schedule with no hours entered yet still had its window considered, and the cursor
            // moving on is what keeps a daily job's window a single new day rather than a re-scan of
            // an ever-growing one.
            schedule.AdvanceCursor(lastDay.AddDays(1), now);
            await schedules.SaveAsync(schedule, cancellationToken);
        }

        return new AvailabilityMaterialized(daysConsidered, daysSkipped, slotsInserted);
    }

    private IEnumerable<Event> GenerateDay(
        BookingCalendar calendar,
        Worker worker,
        WorkerSchedule schedule,
        DateOnly day,
        IReadOnlyList<WorkingHoursRule> workerRules,
        DateTimeOffset now)
    {
        var slotLength = TimeSpan.FromMinutes(schedule.SlotMinutes);
        var buffer = TimeSpan.FromMinutes(schedule.BufferMinutes);

        foreach (var window in WindowsFor(calendar, schedule, day, workerRules))
        {
            foreach (var slot in SlotGrid.Fill(window, slotLength, buffer))
            {
                // A first run at three in the afternoon must not publish this morning's slots.
                // Event.Claim would refuse them anyway, so they would be inert rows a customer can
                // see and cannot book - which is worse than an absence.
                if (slot.EndsAt <= now)
                {
                    continue;
                }

                yield return Event.Materialize(
                    new EventId(idGenerator.NewId(now)),
                    calendar.TenantId,
                    calendar.Id,
                    worker.Id,
                    slot,
                    day,
                    now);
            }
        }
    }

    /// <summary>
    /// The one or zero working windows <paramref name="day"/> resolves to, wall clock converted to
    /// absolute time exactly once - the single bridge <c>adr/0049</c> names, unchanged in shape by
    /// this item even though what feeds it now branches on <see cref="WorkerSchedule.Kind"/>.
    ///
    /// <para><b>Weekly</b> can yield more than one window a day (two rules on one day is how this
    /// product expresses a lunch break); <b>Cycle</b> yields at most one, since a cycle schedule
    /// carries exactly one pair of hours. Both go through the same
    /// <see cref="IWallClockResolver.ToInstantWindow"/> call, so a DST gap that leaves no real time
    /// between the day's edges is handled identically for either kind - the resolver returns
    /// <see langword="null"/> and this method yields nothing for that day.</para>
    /// </summary>
    private IEnumerable<TimeSlot> WindowsFor(
        BookingCalendar calendar, WorkerSchedule schedule, DateOnly day, IReadOnlyList<WorkingHoursRule> workerRules)
    {
        if (schedule.Kind == ScheduleKind.Weekly)
        {
            foreach (var rule in workerRules.Where(rule => rule.DayOfWeek == day.DayOfWeek))
            {
                var window = wallClock.ToInstantWindow(calendar.TimeZone, day, rule.StartsAt, rule.EndsAt);
                if (window is not null)
                {
                    yield return window.Value;
                }
            }

            yield break;
        }

        // Cycle: the day itself is either working or resting - CycleGrid's pure arithmetic, no
        // database and no clock involved in that answer. Only a working day resolves an hours window
        // at all.
        if (!schedule.IsCycleWorkingDay(day))
        {
            yield break;
        }

        var cycleWindow = wallClock.ToInstantWindow(
            calendar.TimeZone, day, schedule.CycleStartsAt!.Value, schedule.CycleEndsAt!.Value);
        if (cycleWindow is not null)
        {
            yield return cycleWindow.Value;
        }
    }
}
