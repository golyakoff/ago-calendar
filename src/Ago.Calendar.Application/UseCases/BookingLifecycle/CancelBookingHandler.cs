using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// An operator cancels a booking that is already confirmed. <c>Booked -&gt; Cancelled</c>.
///
/// <para><b>A separate permission from rejecting, and adr/0016's granularity argument is exactly
/// why.</b> Rejecting inside the veto window costs a customer a slot they were told they had for a
/// few minutes; cancelling a confirmed visit costs them one they have been planning around, possibly
/// for weeks. A tenant may well want a junior operator to do the first and not the second, and that
/// is a data change with two permissions and a code change with one.</para>
///
/// <para><b>A cancelled slot is not re-offered.</b> <see cref="Event"/> has no transition back to
/// <see cref="EventStatus.Available"/> - `20-01` declined to build one, and `20-03` and `20-02` both
/// left it alone. So the time frees up in the sense that the no-overlap constraint stops covering it,
/// and nothing re-materialises a slot there, because the materialiser only ever fills days with no
/// rows at all (adr/0053). Whether a cancellation should re-open the slot is a real product question
/// and it is still nobody's yet; recorded here rather than answered.</para>
///
/// <para><b>`25-63`: a push only when this cancellation actually left PendingConfirmation.</b> Unlike
/// <see cref="RejectBookingHandler"/>, whose <see cref="Event.Reject"/> only ever runs on a still-pending
/// row, this handler's own <see cref="Event.Cancel"/> accepts <see cref="EventStatus.Booked"/> too - and
/// a booking cancelled out of <c>Booked</c> was not in the pending queue for this item's own push to
/// report as having left. <see cref="HandleAsync"/> reads the group's status before calling
/// <see cref="Event.Cancel"/> for exactly this reason: after the call every row already reads
/// <see cref="EventStatus.Cancelled"/>, which cannot distinguish the two starting states any more.</para>
/// </summary>
public sealed class CancelBookingHandler(
    IEventRepository events,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(CancelBooking command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingCancel, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingCancel);
        }

        var booking = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (booking is null)
        {
            return BookingLifecycleErrors.NotFound(command.EventId);
        }

        if (booking.TenantId != command.TenantId)
        {
            return BookingLifecycleErrors.WrongTenant(command.EventId);
        }

        // `20-18`: the route names one slot, which may be any member of the run - resolve the whole
        // group before transitioning anything. A never-claimed row (BookingId null) is its own group
        // of one, which is also what Event.Cancel's own state check then correctly refuses.
        var group = await events.ListByBookingIdAsync(booking.BookingId ?? booking.Id, cancellationToken);
        var now = clock.UtcNow;

        // `25-63`: captured before Cancel() below overwrites it on every row - see this class's own
        // remarks on why "was this group pending" has to be read here rather than after.
        var wasPending = group.Count > 0 && group[0].Status == EventStatus.PendingConfirmation;

        try
        {
            foreach (var slot in group)
            {
                // Event.Cancel accepts PendingConfirmation as well as Booked, which is deliberate on
                // the aggregate's part: an operator looking at a queue does not always know which side
                // of the deadline a row is on, and refusing on that basis would produce an error the
                // operator cannot act on. The permission is what separates the two acts, not the state
                // machine. Every row of the run is cancelled together, in memory, before anything is
                // saved - so a row that refuses (already cancelled by a previous partial attempt, say)
                // aborts the whole group rather than leaving some rows transitioned and others not.
                slot.Cancel(now);
            }
        }
        catch (InvalidEventStateException exception)
        {
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }

        // See RejectBookingHandler for why EventCancelled itself is not staged to the outbox, and its
        // own remarks (mirrored here) for the different, narrower BookingPendingStateChanged fact
        // `25-63` does stage - only when it is true, per this class's own doc comment above.
        if (wasPending)
        {
            outbox.Enqueue(BookingPendingStateChangedMapper.ToEnvelope(
                booking.BookingId ?? booking.Id, command.TenantId, group[0].Status.ToString(), now, idGenerator));
        }

        foreach (var slot in group)
        {
            slot.ClearDomainEvents();
        }

        try
        {
            await events.SaveRangeAsync(group, cancellationToken);
        }
        catch (EventConcurrencyConflictException)
        {
            return BookingLifecycleErrors.ConcurrencyConflict(command.EventId);
        }

        return Result.Success();
    }
}
