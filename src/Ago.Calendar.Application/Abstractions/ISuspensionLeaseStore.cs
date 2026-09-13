using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-08`/`adr/0149` rule 1/`adr/0166`: applies `ago-chat`'s own `TenantSuspensionChanged` lease
/// instant to this product's tenancy row - the consumer half of the crossing, the identical role
/// <see cref="IWorkerQuotaGrantStore"/> plays for the granted quota (`22-07`).
///
/// <para><b>Deliberately simpler than <see cref="IWorkerQuotaGrantStore"/>: no second aggregate, no
/// lock.</b> A quota's own application spans <see cref="Tenant"/> and <see cref="Worker"/> together
/// (deactivating whichever workers become the excess), and needs a lock held across a
/// read-decide-write sequence because <c>IWorkerRepository.TryAddWithinQuotaAsync</c> is a real
/// contender racing the same row. This write touches <see cref="Tenant"/> alone and has no contender
/// of its own to close a race against - <see cref="Infrastructure.Postgres.BookingStore.TryBookAsync"/>
/// only ever *reads* <see cref="Tenant.SuspensionValidUntil"/>, live, inside its own claim; it never
/// writes it. An ordinary EF update, one implicit transaction, the same shape
/// <see cref="ITenantRepository"/>'s own single-aggregate writes already use.</para>
/// </summary>
public interface ISuspensionLeaseStore
{
    /// <summary>Sets <paramref name="tenantId"/>'s current lease instant to exactly
    /// <paramref name="validUntil"/> - <see langword="null"/> clears it (the explicit-lift path's own
    /// immediate correction, `adr/0149` rule 1), a real instant sets or renews it. A snapshot, not a
    /// delta - <see cref="Tenant.ApplySuspensionLease"/>'s own remarks state why a redelivery or a
    /// periodic renewal both land safely on repeat application.</summary>
    Task ApplyAsync(TenantId tenantId, DateTimeOffset? validUntil, CancellationToken cancellationToken);
}
