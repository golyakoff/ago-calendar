using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>`23-60`/`adr/0147`: the tenant's own audit read - "the merge is recorded ... and is
/// visible afterwards" (the item's own Done-when). The identical shape
/// <see cref="GetPhoneRevealsForTenant"/> already establishes for its own audit trail.</summary>
public readonly record struct GetCustomerMergesForTenant(OperatorId OperatorId, TenantId TenantId, Guid? Before, int? Limit);
