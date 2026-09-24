using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `26-97`'s whole design decision, in one collaborator: <b>a working-hours correction is always
/// allowed, and it always says which already-cut days it did not reach.</b>
///
/// <para><b>The question, and why the other answer was rejected.</b> A calendar's schedule may already
/// have materialised real slots - some already booked - from the rule being corrected or removed. The
/// alternative considered was to refuse the edit outright while any already-materialised day in range
/// still carries a live booking. Three facts in this codebase decided against it.</para>
///
/// <para><i>One.</i> <see cref="WorkingHoursRule"/> has no date range at all. It is
/// <c>(worker, calendar, DayOfWeek, TimeOnly, TimeOnly)</c> - an ordinary week, valid from here on,
/// deliberately not a recurrence language. "Already-materialised days inside the rule's own range" has
/// no referent in the domain; the only real window is the already-cut span
/// <c>[today, <see cref="WorkerSchedule.MaterializeFrom"/>)</c>, which belongs to the *schedule*, not
/// to the rule. On a live calendar that span is almost always non-empty and almost always carries a
/// booking - so refusing on it would make the correction unavailable exactly when it matters, which is
/// the defect `26-97` exists to remove, reworded as a polite refusal.</para>
///
/// <para><i>Two.</i> The codebase has already answered the identical question one noun over.
/// <see cref="SaveWorkerScheduleHandler"/> upserts a worker's whole *template* - kind, slot length,
/// buffer, horizon, and a cycle schedule's own wall-clock hours - with no booking check whatever, and
/// `20-16` (<see cref="RecutConfirmHandler"/>, <see cref="WorkerSchedule.RecutFrom"/>) is the
/// deliberate, human-confirmed path for reconciling days already cut. A weekly-hours edit that refused
/// where a cycle-hours edit does not would be an inconsistency with nothing behind it.</para>
///
/// <para><i>Three, and decisive:</i> an edit here <b>cannot</b> damage a booking, so there is nothing
/// for a refusal to protect. <c>MaterializeAvailabilityHandler</c> is non-destructive by construction -
/// it only inserts into business-local days that have no event row at all, and only forward of the
/// cursor. Correcting a rule changes what *future* cuts produce and leaves every already-cut day
/// exactly as it stands, bookings included. The real hazard is not corruption, it is <b>silence</b>:
/// an operator fixes 09:00 to 19:00, the screen accepts it, and the next three weeks keep selling the
/// wrong hours with nothing on any screen to say so.</para>
///
/// <para><b>So this type removes the silence, which is the item's own one hard constraint - no
/// already-booked slot left silently unreconciled.</b> Every correction returns the days already cut
/// from the old hours, how many live bookings sit on them, and the exact <see cref="DateOnly"/> to
/// hand <c>POST /workers/{id}/schedule/recut/preview</c>. The console and the phone both render that
/// as a required next step rather than a hint, and the date is pre-filled because an operator asked to
/// re-derive it is an operator who will type the wrong one.</para>
///
/// <para><b>Read-only, and it is allowed to be slightly stale.</b> Nothing here is a
/// compare-and-set: the numbers are a report an operator reads, never a value a write decision is
/// taken on, so CLAUDE.md rule 8 is not in play - and the re-cut this points at does its own
/// fingerprint staleness check (<see cref="RecutConfirmHandler"/>) against the world as it is at that
/// moment.</para>
/// </summary>
public sealed class WorkingHoursReconciler(
    IWorkerScheduleRepository schedules,
    IWorkerSlotReadStore slots,
    IWallClockResolver wallClock,
    IClock clock)
{
    /// <param name="affectedDays">Which weekdays this change moves slots on. One for a deletion; for
    /// an edit that also moves the weekday, <b>both</b> the old one (days that would lose their grid)
    /// and the new one (days that would gain one) - reporting only one of the two would understate the
    /// range an operator has to re-cut.</param>
    public async Task<WorkingHoursReconciliation> ComputeAsync(
        TenantId tenantId,
        BookingCalendar calendar,
        WorkerId workerId,
        IReadOnlyCollection<DayOfWeek> affectedDays,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(affectedDays);

        var schedule = await schedules.GetByWorkerIdAsync(workerId, cancellationToken);
        if (schedule is null || schedule.Kind != ScheduleKind.Weekly)
        {
            // No schedule means nothing has ever been cut for this worker. A Cycle schedule means the
            // materialiser ignores WorkingHoursRule rows entirely (see MaterializeAvailabilityHandler's
            // own branch on Kind), so editing one changes nothing anywhere and there is nothing to
            // re-cut - a real state worth reporting as "nothing to do" rather than as an empty list
            // that looks like a failed query.
            return WorkingHoursReconciliation.Nothing;
        }

        // "Today" is a question about the calendar's place, never about UTC - the same single
        // conversion MaterializeAvailabilityHandler makes, for the same reason.
        var today = wallClock.ToLocalDate(calendar.TimeZone, clock.UtcNow);

        // What is already cut is [today, MaterializeFrom), bounded above by the horizon: the job never
        // cuts past today + HorizonDays, so a cursor parked beyond that (a tenant who moved it forward
        // by hand) describes days nothing ever generated.
        var lastCut = schedule.MaterializeFrom.AddDays(-1);
        var horizonEnd = today.AddDays(schedule.HorizonDays);
        if (lastCut > horizonEnd)
        {
            lastCut = horizonEnd;
        }

        if (lastCut < today)
        {
            return WorkingHoursReconciliation.Nothing;
        }

        var days = new List<DateOnly>();
        for (var day = today; day <= lastCut; day = day.AddDays(1))
        {
            if (affectedDays.Contains(day.DayOfWeek))
            {
                days.Add(day);
            }
        }

        if (days.Count == 0)
        {
            return WorkingHoursReconciliation.Nothing;
        }

        // One range read, not one query per day: IWorkerSlotReadStore already assembles exactly this
        // window for `20-15`'s slot screen and `20-16`'s own preview. No contact data and no masking -
        // this is a count, and adr's own `20-12` argument says a caller that needs no contact column
        // must cost the database nothing for one.
        var rows = await slots.GetForWorkerAsync(
            tenantId, workerId, days[0], days[^1], includeContactData: false, mask: false, cancellationToken);

        var affected = days.ToHashSet();
        var liveBookings = rows.Count(row => affected.Contains(row.LocalDate) && HoldsACustomer(row.Status));

        return new WorkingHoursReconciliation(days[0], days, liveBookings);
    }

    /// <summary>The same three statuses <c>RecutPreviewHandler.HoldsACustomer</c> and
    /// <c>CancelBookingHandler</c> use - a customer is attached to the row, whether or not the visit
    /// has already happened. A <see cref="EventStatus.Cancelled"/> row is not one of them: it is
    /// history, and nobody is waiting on it.</summary>
    private static bool HoldsACustomer(EventStatus status) =>
        status is EventStatus.PendingConfirmation or EventStatus.Booked or EventStatus.NoShow;
}

/// <summary>
/// `26-97`: what a working-hours correction did <b>not</b> reach, and the one thing to do about it.
/// Returned on every successful edit and delete, never only on the ones that look risky - a caller
/// that has to ask a second endpoint whether its own write mattered is a caller that will not.
/// </summary>
/// <param name="RecutFrom">The date to pass to <c>POST /workers/{id}/schedule/recut/preview</c>, or
/// <see langword="null"/> when nothing was already cut. Always inside
/// <c>[today, MaterializeFrom)</c>, which is exactly the window
/// <see cref="RecutPreviewHandler"/> accepts, so a caller never has to reason about the two
/// refusals (<c>FromBeforeToday</c>, <c>NotARegression</c>) that guard it.</param>
/// <param name="AlreadyCutDays">Every business-local day in that window this change affects, oldest
/// first. A list rather than a count because the screen showing it is the one an operator uses to
/// decide whether re-cutting is worth the cancellations.</param>
/// <param name="LiveBookingCount">Pending, confirmed and no-show rows sitting on those days. Zero is
/// the common, happy case and means a re-cut would destroy nothing a customer is holding.</param>
public readonly record struct WorkingHoursReconciliation(
    DateOnly? RecutFrom, IReadOnlyList<DateOnly> AlreadyCutDays, int LiveBookingCount)
{
    /// <summary>Nothing was already cut from these hours - the correction is complete on its own and
    /// the operator has no follow-up.</summary>
    public static WorkingHoursReconciliation Nothing => new(null, [], 0);
}
