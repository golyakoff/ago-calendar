namespace Ago.Calendar.Contracts;

/// <summary>
/// <b>This booking is on.</b> A past-tense fact, published after the transition it describes has
/// committed (CLAUDE.md rule 4) - either because the operator veto window closed with nobody acting,
/// which is the ordinary case, or because an operator confirmed early.
///
/// <para>This product's first integration event, and its contract is stated here rather than in
/// `20-05` on purpose: `20-05` is the item that wires an SMS consumer to it, and a consumer that has
/// to invent the contract it consumes will invent one shaped around its own convenience. Documented
/// in <c>messaging.md</c>'s Topics table alongside every other event in this codebase.</para>
///
/// <para><b>Ids for anything with a life of its own, values for what is immutably true of this
/// booking.</b> That is the rule the field list follows.
/// <see cref="CustomerId"/> resolves to whatever the lead card says at the moment a consumer reads it
/// - including "no longer there", which is the correct answer after an erasure. A copied name or
/// phone number would be a snapshot that outlives the row it came from.
/// <see cref="StartsAt"/>/<see cref="EndsAt"/>/<see cref="LocalDate"/> are values because they cannot
/// change: the slot a booking took is fixed at claim time, and there is no reschedule in v1
/// (cancel-and-rebook is the only path). Making a consumer re-read them would be a round trip for
/// data that cannot have moved.</para>
///
/// <para><b>No phone number, and this is a rule rather than an omission.</b> An integration event
/// crosses a broker and is read by consumers this product does not control; `20-05` looks the phone
/// up from <see cref="CustomerId"/> at send time. The same reasoning <c>api-design.md</c> gives for a
/// webhook payload carrying no message body: what leaves the write path is what an erasure request
/// can no longer reach. The outbox table nothing prunes is the concrete cost of getting this
/// wrong.</para>
///
/// <para><b><see cref="CalendarId"/> is here because a human-readable time needs a zone</b>, and the
/// calendar owns the IANA zone id (adr/0049). Without it an SMS consumer could only render UTC to
/// somebody standing in the shop.</para>
/// </summary>
/// <param name="EventId">The booked slot. A slot and the booking that took it are one row
/// (adr/0049), so this is the booking's identity too.</param>
/// <param name="TenantId">Whose booking this is. Carried on the event for the same reason
/// <c>events.tenant_id</c> is carried on the row - a consumer must not have to join to find out which
/// tenant a message concerns.</param>
/// <param name="CalendarId">Resolves the IANA zone a time is rendered in.</param>
/// <param name="CustomerId">Resolves the lead card, and therefore the phone number, at send time.</param>
/// <param name="StartsAt">ISO-8601 with an explicit offset, UTC on the wire (date-and-time.md).</param>
/// <param name="EndsAt">Exclusive, matching <c>TimeSlot</c>'s own half-open bound.</param>
/// <param name="LocalDate">The business-local day, as the shop names it - stored rather than derived
/// (adr/0049) precisely so no consumer re-derives it.</param>
/// <param name="OccurredAt">When the confirmation happened, not when the visit is.</param>
/// <param name="CorrelationId">Threaded through the envelope; a fresh id today, because nothing in
/// this product carries a request-scoped correlation into a background sweep yet.</param>
public sealed record BookingConfirmed(
    Guid EventId,
    Guid TenantId,
    Guid CalendarId,
    Guid CustomerId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
