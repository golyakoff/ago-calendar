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
/// is a person at the shop doing it. <b>And no reschedule</b> - cancel and rebook is the whole
/// mechanism, so nothing here moves a booking to a different slot.</para>
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
