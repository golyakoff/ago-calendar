namespace Ago.Calendar.Domain;

/// <summary>
/// The veto window closed without a rejection, or an operator confirmed early - the visit is on. The
/// second SMS (`20-05`) follows this one, through the outbox, in the same transaction as the
/// transition itself (CLAUDE.md rule 4).
///
/// <para>`20-04` widened this from <c>(EventId, TenantId, CustomerId, Slot, OccurredAt)</c> to carry
/// <see cref="CalendarId"/> and <see cref="LocalDate"/> as well, because it is the item that finally
/// mapped it onto a real integration event and found both missing.</para>
///
/// <para><b><see cref="CalendarId"/></b> because the calendar owns the IANA zone (adr/0049), and a
/// consumer that renders "Tuesday at 14:00" for a human cannot do it from an instant alone. Without
/// this the very first consumer would have to load the <see cref="Event"/> row just to learn which
/// calendar it belongs to - and <see cref="EventClaimed"/> already carries it, so its absence here
/// was an asymmetry rather than a decision.</para>
///
/// <para><b><see cref="LocalDate"/></b> for the reason adr/0049 stored it in the first place: which
/// day a slot belongs to is zone-dependent, and it is written once so that nothing downstream ever
/// re-derives it. A consumer computing the business day from <see cref="Slot"/> plus a zone it looked
/// up is exactly the second derivation that column exists to prevent.</para>
///
/// <para><b>What it deliberately does not carry: the customer's phone number, or any name.</b> This
/// record is mapped onto a contract that crosses a broker to consumers this product does not control.
/// <see cref="CustomerId"/> is a pointer that resolves to whatever the lead card says *now*,
/// including "deleted"; a copied phone number would be personal data that outlives the row it came
/// from, in a table nothing prunes.</para>
/// </summary>
public sealed record EventConfirmed(
    EventId EventId,
    TenantId TenantId,
    CalendarId CalendarId,
    CustomerId CustomerId,
    TimeSlot Slot,
    DateOnly LocalDate,
    DateTimeOffset OccurredAt) : IDomainEvent;
