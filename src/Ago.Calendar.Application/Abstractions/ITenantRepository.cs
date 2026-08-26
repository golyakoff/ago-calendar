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

    /// <summary>
    /// One page of tenant ids in id order, for a background job that has to visit every tenant -
    /// `20-02`'s materialiser, which then asks <see cref="IBookingCalendarRepository.ListPublishedAsync"/>
    /// per tenant so that every calendar read it does is still tenant-scoped.
    ///
    /// <para><b>Keyset, not offset</b> (data-model.md): <c>WHERE id &gt; @after ORDER BY id LIMIT
    /// @limit</c> walks the primary key and costs the same on the last page as on the first, whereas
    /// <c>OFFSET</c> re-reads and discards everything before it and quietly skips rows when the set
    /// changes mid-walk. A job that runs while tenants are being created is exactly where that
    /// matters.</para>
    ///
    /// <para>Ids only, not aggregates: the caller needs a key to scope the next query with, and
    /// materialising every <see cref="Tenant"/> in the product to read one column would be the
    /// same waste <see cref="IEventRepository"/> avoids by not returning rows to compute a
    /// maximum.</para>
    /// </summary>
    Task<IReadOnlyList<TenantId>> ListIdsAsync(TenantId? after, int limit, CancellationToken cancellationToken);

    /// <summary>Persists a new tenant. Commits - every mutating method on this product's repositories
    /// does, because no use case here spans two aggregates yet. When one does (`20-06`'s
    /// provisioning transaction is the likely first), it gets an explicit multi-aggregate port the
    /// way AGO Chat's <c>ISiteRegistrationRepository</c> did, rather than a shared unit-of-work
    /// nobody can see the boundaries of.</summary>
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);
}
