using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Tenant"/>. Declared here rather than in Infrastructure because
/// the dependency rule forbids Application knowing about Npgsql; the alternative - injecting a
/// <c>DbContext</c> into a handler - would make every future use case untestable without a database
/// and would let a handler write SQL, which is the exact coupling ports exist to prevent.
///
/// <para>Two methods, both with a named caller in `20-02`..`20-06`, and no <c>Update</c>/<c>Delete</c>
/// pair for symmetry's sake (clean-architecture.md: a port is shaped by its use case, not by CRUD).
/// Deleting a tenant is not a repository method - it is an erasure procedure across every table in
/// this product, and it will look nothing like this when `20-06` or a privacy item builds it.</para>
/// </summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>Persists a new tenant. Commits - every mutating method on this product's repositories
    /// does, because no use case here spans two aggregates yet. When one does (`20-06`'s
    /// provisioning transaction is the likely first), it gets an explicit multi-aggregate port the
    /// way AGO Chat's <c>ISiteRegistrationRepository</c> did, rather than a shared unit-of-work
    /// nobody can see the boundaries of.</summary>
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);
}
