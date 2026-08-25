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
/// <para>The find-or-create that `20-03` actually performs is not one method here, deliberately.
/// Two customers booking simultaneously with the same new phone number will both find nothing and
/// both insert; the unique index rejects the loser, and the retry finds the winner's row. That is a
/// concurrency decision belonging to the handler that owns the transaction, not something a
/// repository method can hide - hiding it is how a check-then-act ends up in a port's
/// implementation, which is exactly where nobody looks for one.</para>
/// </summary>
public interface ICustomerRepository
{
    Task<Customer?> FindByPhoneAsync(TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken);

    Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken);

    Task AddAsync(Customer customer, CancellationToken cancellationToken);

    Task SaveAsync(Customer customer, CancellationToken cancellationToken);
}
