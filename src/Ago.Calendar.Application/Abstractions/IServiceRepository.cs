using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Service"/>. Two methods, both with a real caller:
/// <see cref="GetByIdAsync"/> is how <c>Worker.Offer</c> gets an aggregate to check the tenant of
/// rather than an id it cannot check, and <see cref="ListForTenantAsync"/> is the configuration
/// screen's own list (`20-06`).
///
/// <para>No availability or pricing query here - a service is configuration, and the customer-facing
/// "what can this worker do for me" read is a projection `20-02`'s read store will serve alongside
/// the free slots, in one query rather than two.</para>
/// </summary>
public interface IServiceRepository
{
    Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Service>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(Service service, CancellationToken cancellationToken);
}
