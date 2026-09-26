using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// An operator vetoes a pending booking inside the confirmation window.
/// <c>PendingConfirmation -&gt; Cancelled</c>.
///
/// <para><b>The queue's only action, and that is the design rather than an omission.</b> Confirmation
/// is what happens when nobody acts, so the operator-facing verb is <i>reject</i>: the queue is a
/// veto list, not an approval list. A shop that never opens the console still has every booking
/// confirmed, which is the property the whole two-step mechanic exists to give the customer.</para>
/// </summary>
public readonly record struct RejectBooking(OperatorId OperatorId, TenantId TenantId, EventId EventId);

/// <summary>
/// An operator cancels a booking that is already confirmed. <c>Booked -&gt; Cancelled</c>.
///
/// <para><b>No customer self-service cancel in v1</b>, stated here because it is a product decision
/// and not a missing endpoint: the product spec rules out an SMS link that cancels, so the only path
/// is a person at the shop doing it. Cancel itself moves nothing to a new slot; `26-208`/`adr/0187`
/// added an operator <see cref="RescheduleBooking"/> for that, expressed as cancel-old + claim-new -
/// so a reschedule <i>contains</i> a cancel, but this command is still only the terminal one.</para>
/// </summary>
public readonly record struct CancelBooking(OperatorId OperatorId, TenantId TenantId, EventId EventId);

/// <summary>
/// An operator records that a confirmed visit did not happen. <c>Booked -&gt; NoShow</c>, and only
/// after the slot has ended - <c>Event.MarkNoShow</c> enforces that, because a no-show is a statement
/// about something that did not happen and cannot be made about a visit that has not had its chance.
///
/// <para>The flag is raw material and nothing reads it yet. The product spec names a pre-payment
/// requirement for customers with a no-show history as the rule it eventually feeds; `20-04` builds
/// the flag and the count, and no enforcement.</para>
/// </summary>
public readonly record struct MarkNoShow(OperatorId OperatorId, TenantId TenantId, EventId EventId);

/// <summary>
/// `26-181`/`26-175` slice A: an operator accepts a pending booking right now, instead of waiting for
/// `ExpiredBookingConfirmer`'s sweep to do it once the veto window closes. <c>PendingConfirmation -&gt;
/// Booked</c> - the identical transition the sweep already runs, just triggered early by a person
/// instead of a deadline.
///
/// <para><b>Skips the veto window rather than shortening it.</b> The window exists to give the
/// operator time to decide; an operator who presses "Подтвердить" has already decided, so
/// <see cref="Event.Confirm"/> clears <c>ConfirmationDeadline</c> unconditionally and there is nothing
/// left for a later sweep tick to add. `docs/design/26-175-booking-lifecycle-actions.md` §3.3
/// (product question 4) is where the author ratified this rather than a shortened window.</para>
/// </summary>
public readonly record struct ConfirmBooking(OperatorId OperatorId, TenantId TenantId, EventId EventId);

/// <summary>
/// `26-208`/`adr/0187`: an operator moves a <see cref="EventStatus.Booked"/> booking to a new time -
/// «Перенести оператором». Same worker, same service, time-only for v1. Expressed as cancel-old +
/// claim-new in one transaction (`adr/0187` §Decision), not an in-place mutation of the booking's
/// time: the target time already exists as its own <see cref="EventStatus.Available"/> grid rows, the
/// aggregate offers no transition back to <see cref="EventStatus.Available"/> for the vacated slot,
/// and the model cannot express an in-place move honestly.
/// </summary>
/// <param name="EventId">The old booking - any member of its run, exactly like the other lifecycle
/// commands; the route's own <c>{bookingId}</c>.</param>
/// <param name="NewStartEventId">The <see cref="EventStatus.Available"/> grid slot the operator picked
/// as the new start, by its own id - the console/Android day grid already carries every slot's event
/// id (<c>WorkerSlotResponse</c>), and naming the target slot by id is the same contract
/// <c>BookEvent</c> uses for the slot a visitor claims. The handler resolves the whole new run from it
/// via <c>ConsecutiveRunFinder</c>. Named by id rather than by a wall-clock instant so no timezone
/// conversion is needed to find the target day's grid, and so there is no "find a slot at this time"
/// read to go stale ahead of the claim - the id <i>is</i> the target row.</param>
public readonly record struct RescheduleBooking(
    OperatorId OperatorId, TenantId TenantId, EventId EventId, EventId NewStartEventId);
