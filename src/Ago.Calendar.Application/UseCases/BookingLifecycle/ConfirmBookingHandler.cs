using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// `26-181`/`26-175` slice A: an operator accepts a pending booking right now.
/// <c>PendingConfirmation -&gt; Booked</c>.
///
/// <para><b>The same code path the sweep uses, not a parallel one.</b> <see cref="Event.Confirm"/> was
/// already built (`20-04`) to accept an early operator confirm as well as the sweep's own deadline-driven
/// one - its own doc comment says so. Until this item, nothing outside <c>ExpiredBookingConfirmer</c>
/// called it. This handler is the missing caller, and it stages the identical two outbox rows the sweep
/// stages, built by the same two mappers (<see cref="BookingConfirmedMapper"/>,
/// <see cref="BookingPendingStateChangedMapper"/>), from the same anchor-row <see cref="EventConfirmed"/>
/// and the same run-wide <c>groupEndsAt</c>. A future `20-05` SMS consumer reading
/// <see cref="Ago.Calendar.Contracts.BookingConfirmed"/> cannot tell, and must not need to tell, whether
/// the sweep or an operator produced it - one confirmation contract, two triggers.</para>
///
/// <para><b>Application, not Infrastructure.</b> This is uncontended, single-actor, request-scoped work
/// - one operator, load-mutate-save, with <see cref="Event.Confirm"/> itself enforcing the state
/// precondition - exactly the division <c>IBookingStore</c>'s own remarks draw between the raw,
/// contended atomic claim and the ordinary aggregate writes (<see cref="RejectBookingHandler"/>,
/// <see cref="CancelBookingHandler"/>, <see cref="MarkNoShowHandler"/>, and now this one). Folding
/// confirm into <c>ExpiredBookingConfirmer</c> instead would put a request-scoped, operator-triggered
/// action inside a background-job Infrastructure adapter, and would couple a console endpoint to
/// <c>AgoCalendarDbContext</c> raw Npgsql it has no reason to touch - the alternative the design pass
/// (`docs/design/26-175-booking-lifecycle-actions.md` §3.1) considered and rejected.</para>
///
/// <para><b>This handler races the sweep, and losing is an ordinary outcome - the identical race
/// <see cref="RejectBookingHandler"/>'s own remarks describe, generalised to a different destination
/// state.</b> An operator can press confirm in the same instant a sweeper claims the same booking's
/// anchor row. Whichever commits first wins: if the sweep wins, this handler's
/// <see cref="IEventRepository.SaveRangeAsync"/> is rejected by the row's <c>xmin</c>
/// (<see cref="EventConcurrencyConflictException"/> - "the booking changed under you"). If this handler
/// wins, the sweep's own claim predicate (<c>status = 'PendingConfirmation'</c>) simply no longer
/// matches the row, and it moves on having confirmed nothing for this booking. Either way the outcome is
/// exactly one <see cref="Ago.Calendar.Contracts.BookingConfirmed"/> staged, never two, and no new lock
/// is needed for it.</para>
///
/// <para><b>Idempotent: confirming an already-<see cref="EventStatus.Booked"/> booking is refused, not
/// silently accepted.</b> <see cref="Event.Confirm"/> only transitions
/// <see cref="EventStatus.PendingConfirmation"/>, so a second confirm - whether the caller double-clicked
/// or the sweep already won the race - throws <see cref="InvalidEventStateException"/>, which this
/// handler turns into the same <c>booking.invalid_state</c> failure <see cref="RejectBookingHandler"/>
/// reports for the equivalent already-confirmed case. A caller can retry safely: the second attempt
/// reports a clear, actionable refusal rather than staging a second confirmation.</para>
/// </summary>
public sealed class ConfirmBookingHandler(
    IEventRepository events,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(ConfirmBooking command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingConfirm, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingConfirm);
        }

        var booking = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (booking is null)
        {
            return BookingLifecycleErrors.NotFound(command.EventId);
        }

        // The tenant on the row, not the tenant on the token - see RejectBookingHandler's own remarks
        // for why skipping this would be a cross-tenant leak the permission check alone cannot catch.
        if (booking.TenantId != command.TenantId)
        {
            return BookingLifecycleErrors.WrongTenant(command.EventId);
        }

        // `20-18`: the route names one slot, which may be any member of the run - resolve the whole
        // group before confirming anything, the same "one id in, whole booking acted on" shape every
        // other lifecycle handler already uses. Ordered by StartsAt explicitly, matching
        // ExpiredBookingConfirmer's own grouping: this handler needs the run's first row (the anchor,
        // for EventConfirmed) and its last (for groupEndsAt), not merely the set.
        var group = (await events.ListByBookingIdAsync(booking.BookingId ?? booking.Id, cancellationToken))
            .OrderBy(slot => slot.StartsAt)
            .ToList();
        var now = clock.UtcNow;

        EventConfirmed? anchorConfirmed = null;
        try
        {
            foreach (var slot in group)
            {
                // The state machine still runs, for every row of the run - Event.Confirm's own
                // precondition is what turns "already confirmed by the sweep" or "already
                // cancelled/rejected" into the ordinary refusal below, rather than this handler having
                // to re-derive the check.
                slot.Confirm(now);

                if (slot.Id == slot.BookingId)
                {
                    anchorConfirmed = slot.DomainEvents.OfType<EventConfirmed>().Single();
                }
            }
        }
        catch (InvalidEventStateException exception)
        {
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }

        // Exactly one BookingConfirmed per booking, from the anchor's own domain event - the identical
        // "once per booking, not per slot" reasoning ExpiredBookingConfirmer's own staging follows, so a
        // three-slot run confirmed here does not become three identical texts to one customer under
        // `20-05`.
        var groupEndsAt = group[^1].EndsAt;
        outbox.Enqueue(BookingConfirmedMapper.ToEnvelope(anchorConfirmed!, idGenerator, groupEndsAt));

        // `25-63`: this booking just left PendingConfirmation - once per booking, the same push
        // RejectBookingHandler stages for its own transition out of the pending state. Unlike
        // CancelBookingHandler, Event.Confirm only ever runs on a still-pending row, so this push is
        // unconditional here - there is no "was this already Booked" case to guard against.
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
