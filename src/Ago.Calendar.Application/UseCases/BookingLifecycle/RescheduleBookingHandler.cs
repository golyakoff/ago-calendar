using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// `26-208`/`adr/0187`: an operator moves a confirmed booking to a new time. Same worker, same
/// service, time-only for v1. <c>Booked</c> old run -&gt; <c>Cancelled</c>, and a new run claimed
/// straight into <c>Booked</c> at the target time, in one transaction.
///
/// <para><b>Why reschedule is a handler-orchestrated composition, not a new domain method.</b> The two
/// halves this move is made of already exist on the aggregate and are already proven under their own
/// races: <see cref="Event.Cancel"/> (<c>Booked -&gt; Cancelled</c>, the exact transition
/// <see cref="CancelBookingHandler"/> drives) and the atomic slot claim (<c>Available -&gt;</c> a
/// booked run, the exact write <see cref="IBookingStore"/> drives). A reschedule adds no new domain
/// state and no new transition - it composes the two and requires only that they commit together
/// (`adr/0187` §Consequences). A new <c>Event.Reschedule</c> domain method would have to re-implement
/// both preconditions and could still not span the two rows' transaction (an aggregate sees only
/// itself; the new run is a different set of rows entirely), so the composition belongs in a use case,
/// with the transaction in the store beneath it.</para>
///
/// <para><b>Order of operations.</b> Permission first (a caller with no right never learns whether the
/// id exists); then resolve the old run and require it <see cref="EventStatus.Booked"/> and this
/// tenant's; then resolve the new run over the target day's grid via <see cref="ConsecutiveRunFinder"/>
/// (a courtesy read - rule 8, it only decides <i>which ids to claim</i>); then the store, which is the
/// only step that changes anything and the only place the availability decision is actually made,
/// inside its own transaction's <c>WHERE</c> clause.</para>
///
/// <para><b>Losing the claim race is an ordinary outcome.</b> If the target was taken between this
/// handler's courtesy read and the store's claim, the store rolls the whole transaction back and
/// returns <see langword="false"/>; the reschedule fails with "slot no longer available" and <b>the
/// old booking stays <see cref="EventStatus.Booked"/>, untouched</b> - see
/// <see cref="IBookingRescheduleStore"/>.</para>
/// </summary>
public sealed class RescheduleBookingHandler(
    IEventRepository events,
    IServiceRepository services,
    IWorkerScheduleRepository schedules,
    IBookingRescheduleStore reschedules,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(RescheduleBooking command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingReschedule, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingReschedule);
        }

        var booking = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (booking is null)
        {
            return BookingLifecycleErrors.NotFound(command.EventId);
        }

        // The tenant on the row, not the tenant on the token - the same cross-tenant-leak guard every
        // other lifecycle handler carries (see RejectBookingHandler's own remarks).
        if (booking.TenantId != command.TenantId)
        {
            return BookingLifecycleErrors.WrongTenant(command.EventId);
        }

        // `20-18`: the route names one slot, which may be any member of the old run - resolve the whole
        // group, ordered by start, before deciding anything. group[0] is the anchor (earliest slot,
        // whose Id == BookingId).
        var oldAnchorId = booking.BookingId ?? booking.Id;
        var oldRun = await events.ListByBookingIdAsync(oldAnchorId, cancellationToken);

        // Only a confirmed booking is rescheduled (`adr/0187`: v1 is Booked-only; a still-pending
        // booking is more naturally rejected + rebooked). Checked on the whole run - every row of a run
        // shares one status.
        if (oldRun.Count == 0 || oldRun.Any(slot => slot.Status != EventStatus.Booked))
        {
            return BookingLifecycleErrors.InvalidState(
                $"Only a confirmed booking can be rescheduled; booking {command.EventId.Value} is not Booked.");
        }

        var oldAnchor = oldRun[0];
        if (oldAnchor.ServiceId is null || oldAnchor.PersonId is null)
        {
            // A Booked row always carries both (Event.Claim sets them and never clears them). Defended
            // rather than asserted: the run is about to be re-created and both are load-bearing.
            return BookingLifecycleErrors.InvalidState(
                $"Booking {command.EventId.Value} is missing the service or person it was claimed with.");
        }

        // The target slot the operator picked, named by its own id (RescheduleBooking.NewStartEventId's
        // remarks on why an id and not a wall-clock instant). Its LocalDate is what selects the day's
        // grid - no timezone conversion needed, because the row already carries the resolved local day.
        var target = await events.GetByIdAsync(command.NewStartEventId, cancellationToken);
        if (target is null)
        {
            return BookingLifecycleErrors.SlotNoLongerAvailable();
        }

        // v1 is same-worker, same-service, time-only (`adr/0187`). A target on a different worker or
        // calendar - which also catches a cross-tenant id, since a worker and calendar are
        // tenant-scoped - is refused rather than silently moving across workers.
        if (target.WorkerId != oldAnchor.WorkerId || target.CalendarId != oldAnchor.CalendarId)
        {
            return BookingLifecycleErrors.DifferentWorker();
        }

        var service = await services.GetByIdAsync(oldAnchor.ServiceId.Value, cancellationToken);
        var schedule = await schedules.GetByWorkerIdAsync(oldAnchor.WorkerId, cancellationToken);
        if (service is null || schedule is null)
        {
            // Without the service's duration or the worker's grid numbers there is no run to compute -
            // reported as the ordinary "that time will not work" the operator can act on, never a fault.
            return BookingLifecycleErrors.SlotNoLongerAvailable();
        }

        // Every row of the worker's target day, whatever its status - the courtesy read
        // ConsecutiveRunFinder walks to find where a run of the service's length would fit, starting at
        // the picked slot. The status this handler ultimately trusts is only the claim's own WHERE
        // clause inside the store's transaction; a stale answer here costs nothing more than the
        // ordinary "slot no longer available" below (rule 8).
        var dayEvents = await events.ListForDayAsync(
            oldAnchor.CalendarId, oldAnchor.WorkerId, target.LocalDate, cancellationToken);

        var newRun = ConsecutiveRunFinder.FindRun(
            dayEvents,
            command.NewStartEventId,
            (int)service.Duration.TotalMinutes,
            schedule.SlotMinutes,
            schedule.BufferMinutes,
            schedule.BuffersCountTowardServiceDuration);

        if (newRun is null)
        {
            // No consecutive run of the length this service needs exists starting at the picked slot -
            // too close to the end of the day, a middle slot already taken, or the picked slot itself
            // is not Available. The same lost-race shape a visitor booking reports.
            return BookingLifecycleErrors.SlotNoLongerAvailable();
        }

        var now = clock.UtcNow;

        // The new run's own span, read from the grid the courtesy read already returned - the last
        // slot's end is the run's end, buffers between its slots included. Known before the claim
        // because the grid rows carry it; the claim only decides whether the ids are still Available.
        var lastSlot = dayEvents.Single(slot => slot.Id == newRun[^1]);

        // The one BookingRescheduled envelope, built here (Application - where clean-architecture.md
        // places the domain-to-contract mapping) from the two runs already resolved, and handed to the
        // store to stage on the success path inside the transaction (rule 4). Exactly one event, and
        // deliberately NOT the BookingConfirmed / BookingPendingStateChanged("Booked") pair the
        // ordinary confirm path stages, nor a Cancelled signal for the old run (`adr/0187`).
        var rescheduledEvent = BookingRescheduledMapper.ToEnvelope(
            newBookingId: newRun[0],
            previousBookingId: oldAnchorId,
            tenantId: command.TenantId,
            calendarId: oldAnchor.CalendarId,
            personId: oldAnchor.PersonId.Value,
            previousStartsAt: oldAnchor.StartsAt,
            newStartsAt: target.StartsAt,
            newEndsAt: lastSlot.EndsAt,
            localDate: target.LocalDate,
            occurredAt: now,
            idGenerator);

        var request = new BookingRescheduleRequest(
            PreviousBookingId: oldAnchorId,
            CalendarId: oldAnchor.CalendarId,
            NewEventIds: newRun,
            PersonId: oldAnchor.PersonId.Value,
            ServiceId: oldAnchor.ServiceId.Value,
            OriginConversationId: oldAnchor.OriginConversationId,
            Now: now,
            RescheduledEvent: rescheduledEvent);

        try
        {
            var rescheduled = await reschedules.TryRescheduleAsync(request, cancellationToken);
            if (!rescheduled)
            {
                // The target was taken in the race. The whole transaction rolled back and the old
                // booking stays Booked - IBookingRescheduleStore's own contract.
                return BookingLifecycleErrors.SlotNoLongerAvailable();
            }
        }
        catch (InvalidEventStateException exception)
        {
            // The old run stopped being Booked between the read above and the store's transaction
            // (another operator cancelled it, say). The same ordinary refusal the other handlers give.
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }
        catch (EventConcurrencyConflictException)
        {
            return BookingLifecycleErrors.ConcurrencyConflict(command.EventId);
        }

        return Result.Success();
    }
}
