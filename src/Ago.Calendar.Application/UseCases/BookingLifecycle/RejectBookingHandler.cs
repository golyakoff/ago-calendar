using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
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
/// <para><b><see cref="EventCancelled"/> itself is still not staged, and that stays deliberate.</b>
/// <see cref="Event.Reject"/> raises it, and nothing reads it as a customer-facing fact: the only
/// integration event naming a rejected/cancelled booking's full detail would be for `20-05`'s SMS
/// (still unbuilt, still no named consumer), and staging a contract nobody reads would be a guess at
/// how it wants to tell a customer their booking was refused - a message with quite different wording
/// and urgency from a confirmation.</para>
///
/// <para><b>`25-63` does stage a row here now, and it is a different, narrower fact.</b>
/// <see cref="Ago.Calendar.Contracts.BookingPendingStateChanged"/> says only "this booking left the
/// pending state", with no customer, no phone, no reason - the one real consumer is
/// `Ago.Calendar.Worker`'s own fan-out, pushing every connected operator of the tenant a signal to
/// re-read the queue. That consumer existing is exactly what this class's own remarks above say
/// `EventCancelled` itself is still missing: a real, named reader. The two facts do not collapse into
/// one contract because they answer different questions for different audiences - a customer's SMS
/// needs to know *why* and in what words; the console's queue only needs to know *that*.</para>
/// </summary>
public sealed class RejectBookingHandler(
    IEventRepository events,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
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
        var now = clock.UtcNow;

        try
        {
            foreach (var slot in group)
            {
                slot.Reject(now);
            }
        }
        catch (InvalidEventStateException exception)
        {
            // Already confirmed by the sweep, already cancelled, or never claimed at all. A caller
            // mistake or a race they lost - either way something they can see and act on, not a
            // fault (coding-style.md: exceptions are for the unexpected).
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }

        // `25-63`: this group just left PendingConfirmation - once per booking, not per slot, the
        // identical "one row per booking" reasoning ExpiredBookingConfirmer's own BookingConfirmed
        // staging already follows. Reads `group[0]`'s own already-mutated Status (every row of a run
        // is rejected together, above) rather than re-deriving "Cancelled" from CancellationReason -
        // Event.Reject/Cancel both land on the identical EventStatus.Cancelled, so there is only one
        // string this contract's Status field could ever carry for either.
        outbox.Enqueue(BookingPendingStateChangedMapper.ToEnvelope(
            booking.BookingId ?? booking.Id, command.TenantId, group[0].Status.ToString(), now, idGenerator));

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
