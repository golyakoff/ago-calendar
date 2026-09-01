using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// An operator vetoes a pending booking before its deadline. <c>PendingConfirmation -&gt; Cancelled</c>.
///
/// <para><b>This handler races the sweep, and losing is an ordinary outcome.</b> An operator can
/// click reject in the same second the deadline passes and a sweeper claims the row. Whichever
/// commits first wins: if the sweep does, this handler's save is rejected by the row's <c>xmin</c>
/// and the operator is told the booking changed under them (`20-01` mapped that to
/// <see cref="EventConcurrencyConflictException"/> so no handler sees an ORM type). If this handler
/// does, the sweeper's claim never matches the row - its predicate names
/// <see cref="EventStatus.PendingConfirmation"/>, and the row is <see cref="EventStatus.Cancelled"/>
/// by then. Neither path needs a lock the other has to know about.</para>
///
/// <para><b>No outbox row, and that is deliberate rather than an oversight.</b>
/// <see cref="Event.Reject"/> raises <see cref="EventCancelled"/>, and nothing consumes it: the only
/// integration event this item defines is <c>BookingConfirmed</c>, because it is the only one with a
/// named consumer (`20-05`'s SMS). Staging a contract nobody reads would be a guess at how `20-05`
/// wants to tell a customer their booking was refused - a message with quite different wording and
/// quite different urgency from a confirmation - and inventing it here would be inventing it in the
/// wrong item.</para>
/// </summary>
public sealed class RejectBookingHandler(
    IEventRepository events,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsync(RejectBooking command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingReject, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingReject);
        }

        var booking = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (booking is null)
        {
            return BookingLifecycleErrors.NotFound(command.EventId);
        }

        // The tenant on the row, not the tenant on the token. The permission check above proved the
        // operator holds the right in the tenant they *claimed*; this proves the booking is in it.
        // Skipping the second check would let an operator with a legitimate permission act on another
        // tenant's booking by guessing an id - which is the shape of every cross-tenant bug.
        if (booking.TenantId != command.TenantId)
        {
            return BookingLifecycleErrors.WrongTenant(command.EventId);
        }

        // `20-18`: the route names one slot, which may be any member of the run - resolve the whole
        // group before vetoing anything. Every row of a run shares one ConfirmationDeadline, so this
        // handler races the sweep at the level of the whole group, not one row of it: whichever side
        // commits first wins every row it touches, exactly the single-row race `RejectBookingHandler`'s
        // own remarks already describe, generalised to a set.
        var group = await events.ListByBookingIdAsync(booking.BookingId ?? booking.Id, cancellationToken);

        try
        {
            foreach (var slot in group)
            {
                slot.Reject(clock.UtcNow);
            }
        }
        catch (InvalidEventStateException exception)
        {
            // Already confirmed by the sweep, already cancelled, or never claimed at all. A caller
            // mistake or a race they lost - either way something they can see and act on, not a
            // fault (coding-style.md: exceptions are for the unexpected).
            return BookingLifecycleErrors.InvalidState(exception.Message);
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
