using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases;

/// <summary>
/// Turns one worker's schedule into the <see cref="Event.Materialize"/> rows for one business-local
/// day - resolving the day's working window(s) through <see cref="IWallClockResolver"/> exactly once,
/// then handing the result to <see cref="SlotGrid.Fill"/>.
///
/// <para><b>Extracted from <c>MaterializeAvailabilityHandler</c>, which is where this logic first
/// existed as two private methods.</b> `20-16`'s own re-cut needs to generate a day's slots
/// synchronously, from the same rule, the instant an operator confirms - and a second, hand-copied
/// implementation is exactly the thing that drifts the day two use cases produce differ without either
/// one changing on purpose. A `Handler` type exists to answer one HTTP route or job tick; it is not a
/// library another handler reaches into by widening an access modifier. Moving the shared rule to its
/// own static type, with both callers depending on it, is the same shape <see cref="SlotGrid"/> itself
/// already uses one layer down - the guarantee lives in one place, not in two callers that happen to
/// agree today.</para>
///
/// <para><b>Application, not Domain, and for the same reason <see cref="IWallClockResolver"/> is a
/// port at all.</b> Resolving a wall-clock window needs the tz database, which is ambient machine
/// state Domain must not read (CLAUDE.md rule 2); this type takes the resolver and the id generator as
/// parameters rather than depending on anything ambient itself, so it stays a pure function of its
/// inputs and both ports remain the only place ambient state enters.</para>
/// </summary>
public static class DayGenerator
{
    /// <summary>
    /// The slots one business-local <paramref name="day"/> resolves to under <paramref name="schedule"/>
    /// - every one of them fresh <see cref="EventStatus.Available"/> rows, never inserted or checked
    /// against what already exists on that day. Both callers own that decision themselves: the
    /// materialiser only ever calls this for a day <see cref="IEventRepository.ListMaterializedLocalDatesAsync"/>
    /// said was empty, and `20-16`'s re-cut only ever calls this for a day it has already cleared of
    /// every <see cref="EventStatus.Available"/> and <see cref="EventStatus.Blocked"/> row through
    /// <see cref="IEventRepository.ReplaceDayAsync"/>.
    /// </summary>
    public static IEnumerable<Event> GenerateDay(
        BookingCalendar calendar,
        Worker worker,
        WorkerSchedule schedule,
        DateOnly day,
        IReadOnlyList<WorkingHoursRule> workerRules,
        IWallClockResolver wallClock,
        IIdGenerator idGenerator,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(workerRules);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(idGenerator);

        var slotLength = TimeSpan.FromMinutes(schedule.SlotMinutes);
        var buffer = TimeSpan.FromMinutes(schedule.BufferMinutes);

        foreach (var window in WindowsFor(calendar, schedule, day, workerRules, wallClock))
        {
            foreach (var slot in SlotGrid.Fill(window, slotLength, buffer))
            {
                // A run at three in the afternoon must not publish this morning's slots. Event.Claim
                // would refuse them anyway, so they would be inert rows a customer can see and cannot
                // book - which is worse than an absence.
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
    /// absolute time exactly once - the single bridge <c>adr/0049</c> names.
    ///
    /// <para><b>Weekly</b> can yield more than one window a day (two rules on one day is how this
    /// product expresses a lunch break); <b>Cycle</b> yields at most one, since a cycle schedule
    /// carries exactly one pair of hours. Both go through the same
    /// <see cref="IWallClockResolver.ToInstantWindow"/> call, so a DST gap that leaves no real time
    /// between the day's edges is handled identically for either kind - the resolver returns
    /// <see langword="null"/> and this method yields nothing for that day.</para>
    /// </summary>
    private static IEnumerable<TimeSlot> WindowsFor(
        BookingCalendar calendar,
        WorkerSchedule schedule,
        DateOnly day,
        IReadOnlyList<WorkingHoursRule> workerRules,
        IWallClockResolver wallClock)
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
