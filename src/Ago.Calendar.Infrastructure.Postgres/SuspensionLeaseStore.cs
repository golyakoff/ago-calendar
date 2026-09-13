using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`22-08`'s <see cref="ISuspensionLeaseStore"/> adapter - see that interface's own remarks
/// for why this is an ordinary single-aggregate write through <see cref="ITenantRepository"/>, unlike
/// <see cref="WorkerQuotaGrantStore"/>'s own explicit transaction and row lock.</summary>
public sealed class SuspensionLeaseStore(ITenantRepository tenants) : ISuspensionLeaseStore
{
    public async Task ApplyAsync(TenantId tenantId, DateTimeOffset? validUntil, CancellationToken cancellationToken)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            // The same "a foreign key should have prevented this" judgement WorkerQuotaGrantStore's
            // own remarks make for the identical impossible case - this consumer only ever resolves
            // TenantId from ago-chat's own SiteId (22-03's own "the calendar's tenancy row equals the
            // account id"), and every tenant this product's own module registration ever creates
            // writes that row before anything downstream could reference it.
            throw new InvalidOperationException(
                $"Tenant {tenantId.Value} was not found while applying a suspension lease.");
        }

        tenant.ApplySuspensionLease(validUntil);
        await tenants.SaveAsync(tenant, cancellationToken);
    }
}
