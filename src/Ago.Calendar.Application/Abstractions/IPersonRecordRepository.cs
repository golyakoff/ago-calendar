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
/// <para><b>Nothing here looks a <em>person</em> up by phone.</b> The record is keyed by the opaque
/// person id (<see cref="PersonRecord.PersonId"/>), and a "find the person with this phone" would
/// re-introduce the "a phone is a person" assumption `adr/0147` and `adr/0184` both refuse.
/// <see cref="FindPhoneVerifiedAtAsync"/> is the one deliberate, narrower exception: it answers a
/// question about the <em>number</em> - "has this tenant ever seen this number proven reachable" -
/// and returns an instant, never a record, so nothing a caller could merge or misattribute ever
/// leaves it.</para>
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
