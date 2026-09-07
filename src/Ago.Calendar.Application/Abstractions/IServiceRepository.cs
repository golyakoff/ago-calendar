using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Service"/>. Two methods, both with a real caller:
/// <see cref="GetByIdAsync"/> is how <c>Worker.Offer</c> gets an aggregate to check the tenant of
/// rather than an id it cannot check, and <see cref="ListForTenantAsync"/> is the configuration
/// screen's own list (`20-06`).
///
/// <para>No availability query here, and - even after `23-35` gave <see cref="Service"/> a price and a
/// description - still no customer-facing pricing <i>query</i> either: a service is configuration, and
/// the customer-facing "what can this worker do for me, and what does it cost" read is a projection
/// <see cref="IBookingSurfaceReadStore"/> serves alongside the free slots, in one query rather than
/// two. This port stays the write side; a caller wanting `Price`/`Description` for a screen with more
/// than a handful of services should have its own reason to load whole aggregates to read two
/// fields.</para>
/// </summary>
public interface IServiceRepository
{
    Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Service>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(Service service, CancellationToken cancellationToken);
}
