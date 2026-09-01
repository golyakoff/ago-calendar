using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
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
/// </summary>
public sealed class CancelBookingHandler(
    IEventRepository events,
    IPermissionChecker permissions,
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
                slot.Cancel(clock.UtcNow);
            }
        }
        catch (InvalidEventStateException exception)
        {
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }

        // See RejectBookingHandler for why EventCancelled is not staged to the outbox.
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
