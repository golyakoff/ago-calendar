using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// Three aggregates, one <c>SaveChangesAsync</c>.
///
/// <para><b>No explicit <c>BeginTransaction</c>, and that is not carelessness.</b> EF Core wraps a
/// single <c>SaveChangesAsync</c> in one transaction already, so opening another would add a nesting
/// level and change nothing about atomicity. What makes this a real transaction boundary is that all
/// three <c>Add</c> calls happen before the one save - which is why this adapter exists at all
/// instead of three repository calls that each commit.</para>
/// </summary>
public sealed class TenantProvisioningStore(AgoCalendarDbContext db) : ITenantProvisioningStore
{
    public async Task RegisterAsync(
        Tenant tenant, Role role, Operator @operator, CancellationToken cancellationToken)
    {
        db.Tenants.Add(tenant);
        db.Roles.Add(role);
        db.Operators.Add(@operator);
        await db.SaveChangesAsync(cancellationToken);
    }
}
