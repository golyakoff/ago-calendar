using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="PersonRecord"/> - the calendar's thin, person-id-keyed
/// operational record (`adr/0184`).
///
/// <para><b>No find-or-create here, on purpose.</b> The booking path's own upsert lives on
/// <see cref="IBookingStore"/>, because it shares a transaction with the slot claim and that
/// transaction has to belong to something a reader can see - the same reasoning the old
/// <c>ICustomerRepository</c> gave. What remains on this port is what an <em>operator</em> does to
/// the record - read it, confirm a phone by calling - uncontended single-actor work where a
/// load-mutate-save is exactly right.</para>
///
/// <para><b>This port itself still looks a <em>person</em> up by phone nowhere - `adr/0188` did not
/// change that.</b> The record is keyed by the opaque person id (<see cref="PersonRecord.PersonId"/>),
/// and a method here that took a phone and handed back <em>the</em> matching <see cref="PersonRecord"/>
/// would still be an identity-asserting lookup - "this number resolves to this person" - the exact
/// assumption `adr/0147` refuses. <see cref="FindPhoneVerifiedAtAsync"/> stays the one deliberate,
/// narrower exception below: a question about the <em>number</em>, answered with an instant, never a
/// record.</para>
///
/// <para><b>`26-268`§2a/`adr/0188` added a by-phone read, but it lives elsewhere, on purpose.</b>
/// Phone-based client recognition - "does a client with this number already exist, so the operator can
/// reuse them instead of minting a new one" - is a real, wanted capability, and `adr/0188` is explicit
/// that it does not reopen the question `adr/0147` closed: the read
/// (<c>Application.Abstractions.IPersonRecognitionReadStore</c>) is screen-shaped, can return more than
/// one candidate for one number, and never itself decides which - it only *surfaces*. The operator's
/// own choice («Это он» / a list row / «Новый клиент») is the human confirmation `adr/0147` requires
/// before any write ever reuses an id. That is a read-model concern (adr/0004: a store shaped for one
/// screen, not an aggregate repository), which is why it is a sibling port next to this one rather than
/// a second method here - adding it to <see cref="IPersonRecordRepository"/> would blur the one
/// invariant this interface exists to hold: nothing reachable from <em>this</em> port ever turns a
/// phone into a single person.</para>
/// </summary>
public interface IPersonRecordRepository
{
    Task<PersonRecord?> GetByIdAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>
    /// `20-10` Scope item (b), kept under `adr/0184`: the earliest <see cref="PersonRecord.PhoneVerifiedAt"/>
    /// any of this tenant's records holds for <paramref name="phone"/>, or <see langword="null"/> if the
    /// number was never verified here. Tenant-scoped, always - a verification at another shop is not
    /// this shop's fact. Read by <c>PhoneVerificationAssertionResolver</c> before asking for a fresh
    /// code; the returned instant is then snapshotted onto whichever person record the booking writes.
    /// </summary>
    Task<DateTimeOffset?> FindPhoneVerifiedAtAsync(TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken);

    Task AddAsync(PersonRecord record, CancellationToken cancellationToken);

    Task SaveAsync(PersonRecord record, CancellationToken cancellationToken);
}
