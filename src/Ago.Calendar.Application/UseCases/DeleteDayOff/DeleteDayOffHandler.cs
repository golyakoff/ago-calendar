using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.DeleteDayOff;

/// <summary>
/// Takes a materialised day out of circulation: every unclaimed slot on it disappears and a single
/// <see cref="EventStatus.Blocked"/> row takes their place.
///
/// <para><b>Why a day off leaves a row behind instead of leaving the day empty.</b> This is the one
/// decision that makes manual editing survive the next job tick, and deleting the slots alone would
/// have got it exactly wrong. The materialiser regenerates any day that has no event row on it, so
/// an emptied day is a day the very next run refills - the tenant's "I am closed next Tuesday" would
/// hold until the small hours and then quietly undo itself. A blocked row is not a marker invented
/// to defeat the job, either: it is the literal truth ("this worker is unavailable for this time"),
/// it is what <see cref="Event.BlockOut"/> already exists for, and it participates in the no-overlap
/// constraint, so nothing can later be materialised or booked across it even if the day-level check
/// were somehow bypassed. The alternative - a separate <c>schedule_exceptions</c> table the
/// materialiser consults - is the declarative exception model the product spec deliberately replaced
/// with direct editing, and it would put a second source of truth next to the rows.</para>
///
/// <para><b>Why the read below is a courtesy and the delete is the guarantee.</b> The check for
/// existing bookings runs against a snapshot, so a customer could claim a slot between it and the
/// write - a check-then-act, and `6-09` is what happens to those. It is kept because a clear
/// "cancel the booking first" beats a constraint violation for the ordinary case, but nothing rests
/// on it: <see cref="IEventRepository.ReplaceDayAsync"/> can only delete unclaimed rows, so the
/// booking survives regardless, and the blocking row then overlaps it and the whole transaction is
/// refused. The failure mode of the race is a retry message, never a lost booking.</para>
/// </summary>
public sealed class DeleteDayOffHandler(
    IEventRepository events,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(DeleteDayOff command, CancellationToken cancellationToken)
    {
        var day = await events.ListForDayAsync(
            command.CalendarId, command.WorkerId, command.LocalDate, cancellationToken);

        if (day.Count == 0)
        {
            // Nothing generated for this day yet. Succeeding here would be a lie: with no row to
            // leave behind, the next materialisation run would fill the day in and the tenant would
            // have been told they were closed when they were not. Declaring a day off beyond the
            // horizon needs a durable statement the materialiser reads, which is `20-04`/`20-06`
            // scope, not a silent no-op here.
            return AvailabilityErrors.DayNotMaterialized(command.LocalDate);
        }

        if (day.Any(HoldsACustomer))
        {
            return AvailabilityErrors.DayHasBookings(command.LocalDate);
        }

        var replaceable = day.Where(Replaceable).ToList();
        if (!replaceable.Exists(@event => @event.Status == EventStatus.Available))
        {
            // Nothing bookable is left on this day, so the tenant's intent is already the world's
            // state and there is nothing to write. Two shapes reach here and both are genuinely
            // done: a day already blocked (a second click, or a retried request - idempotent rather
            // than an error), and a day holding only cancelled rows, which the materialiser will
            // skip for the same reason it skips any non-empty day.
            return Result.Success();
        }

        var now = clock.UtcNow;

        // The blocking row spans exactly what it replaces, rather than the whole calendar day. Two
        // reasons: a worker whose Tuesday was 09:00-18:00 is not thereby unavailable at midnight, and
        // a span derived from real rows needs no second wall-clock conversion - the instants are
        // already resolved and stored, so this handler never touches a time zone at all.
        var span = new TimeSlot(replaceable.Min(e => e.StartsAt), replaceable.Max(e => e.EndsAt));

        var closure = Event.BlockOut(
            new EventId(idGenerator.NewId(now)),
            replaceable[0].TenantId,
            command.CalendarId,
            command.WorkerId,
            span,
            command.LocalDate,
            now);

        try
        {
            await events.ReplaceDayAsync(
                command.CalendarId, command.WorkerId, command.LocalDate, [closure], cancellationToken);
        }
        catch (SlotOverlapException)
        {
            // Somebody claimed a slot on this day between the read above and the write. The claimed
            // row was not deleted (it is not deletable), so the closure overlapped it and Postgres
            // refused the transaction whole. Nothing changed; the operator is told to look again.
            return AvailabilityErrors.DayChangedConcurrently(command.LocalDate);
        }

        return Result.Success();
    }

    private static bool HoldsACustomer(Event @event) =>
        @event.Status is EventStatus.PendingConfirmation or EventStatus.Booked or EventStatus.NoShow;

    private static bool Replaceable(Event @event) =>
        @event.Status is EventStatus.Available or EventStatus.Blocked;
}
