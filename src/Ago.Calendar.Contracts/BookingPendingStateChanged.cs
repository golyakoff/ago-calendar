namespace Ago.Calendar.Contracts;

/// <summary>
/// `25-63`: a booking entered or left the pending-confirmation state. The one integration event this
/// item defines, staged from every real transition that crosses that boundary - a claim
/// (<c>PendingConfirmation</c>, entering), the sweep or an operator confirming early
/// (<c>Booked</c>, leaving), and an operator rejecting or cancelling a still-pending run
/// (<c>Cancelled</c>, leaving). Deliberately narrower than <see cref="BookingConfirmed"/>: this
/// carries no customer, no phone, no slot time - the one real consumer (`Ago.Calendar.Worker`'s own
/// fan-out, pushing to every connected operator of the tenant) exists only to tell the console's
/// pending-bookings queue "something changed, re-read it", and the queue's own read
/// (<c>GetPendingBookingsForTenantHandler</c>) is already the permission-checked, contact-masking
/// source of truth - duplicating any of that onto the wire here would be a second copy of an answer
/// that can drift from the first, for no consumer that needs it.
/// </summary>
/// <param name="EventId">The booking's own anchor id (<c>Event.BookingId</c>) - a hint for a future
/// consumer that wants to act on one specific booking; today's only consumer ignores it and re-reads
/// the whole queue.</param>
/// <param name="TenantId">Whose queue changed - the fan-out's own recipient key
/// (<c>Ago.Calendar.Application.Realtime.PrincipalKeys.ForTenant</c>).</param>
/// <param name="Status">The status the transition landed on: <c>"PendingConfirmation"</c>,
/// <c>"Booked"</c> or <c>"Cancelled"</c> - <c>Ago.Calendar.Domain.EventStatus</c>'s own member
/// names, kept as a string rather than a shared enum because Contracts must not reference Domain
/// (clean-architecture.md).</param>
/// <param name="OccurredAt">When the transition committed.</param>
/// <param name="CorrelationId">A fresh id, minted at the transition - the same "no request-scoped
/// correlation reaches a background sweep" reasoning <see cref="BookingConfirmed"/>'s own remarks
/// give, and true here for a claim's own request-scoped call too: this event can fire more than once
/// for the same booking (entering, then later leaving), so it cannot reuse one correlation id across
/// both without implying a causal link between two unrelated operator actions.</param>
public sealed record BookingPendingStateChanged(
    Guid EventId,
    Guid TenantId,
    string Status,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
