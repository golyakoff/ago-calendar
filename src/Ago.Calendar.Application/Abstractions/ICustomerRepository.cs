using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Customer"/> - the lead card.
///
/// <para><see cref="FindByPhoneAsync"/> takes the tenant as well as the phone, and that pair is the
/// port's whole point: a lookup by phone alone would be a cross-tenant read that happens to be
/// spelled like an ordinary one. The unique index underneath is <c>(tenant_id, phone)</c> for the
/// same reason.</para>
///
/// <para><b>The find-or-create `20-03` needed is not here, and the reason `20-01` gave was only half
/// right.</b> This port predicted a read-then-insert-with-retry and refused to hide it, on the
/// grounds that a repository method concealing a check-then-act is a check-then-act nobody will ever
/// find. That reasoning still holds - and `20-03` avoided it a different way: the lead card is
/// upserted, <c>INSERT ... ON CONFLICT (tenant_id, phone) DO UPDATE</c>, which has no check to hide
/// because Postgres arbitrates against the unique index inside the one statement. It lives on
/// <see cref="IBookingStore"/> rather than here because it shares a transaction with the slot claim,
/// and that transaction has to belong to something a reader can see.</para>
///
/// <para>What remains on this port is what an *operator* does to a lead card - look one up, read it,
/// edit it - which is uncontended single-actor work where a load-mutate-save is exactly right.</para>
/// </summary>
public interface ICustomerRepository
{
    Task<Customer?> FindByPhoneAsync(TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken);

    Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken);

    Task AddAsync(Customer customer, CancellationToken cancellationToken);

    Task SaveAsync(Customer customer, CancellationToken cancellationToken);
}
