using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Operator"/>.
///
/// <para><see cref="FindByExternalSubjectIdAsync"/> is the port this product's own
/// <c>OperatorIdentityClaimsTransformation</c> calls (`20-06`, adr/0022's shape copied per
/// adr/0027) - a validated Keycloak <c>sub</c> in, this product's own operator row out. It is a
/// separate method rather than a general "find by any column" because it is the only lookup that
/// ever has a bare string as its key, and its uniqueness is backed by a real partial unique index.
/// </para>
/// </summary>
public interface IOperatorRepository
{
    Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken);

    /// <summary>Resolves a Keycloak subject to this product's own operator. Never a Chat operator -
    /// the same person's Chat row lives in a different database this product cannot reach
    /// (adr/0027).</summary>
    Task<Operator?> FindByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken);

    /// <summary>`20-12`: every operator of one tenant - the role-assignment screen's own list, and the
    /// same tenant-scoping discipline every other <c>ListForTenantAsync</c> in this product already
    /// holds itself to.</summary>
    Task<IReadOnlyList<Operator>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(Operator @operator, CancellationToken cancellationToken);

    /// <summary>`20-12`: the write side `Operator.Grant`/`Operator.Revoke` never had a caller for
    /// before now. Named <c>SaveAsync</c> rather than <c>UpdateAsync</c> to match every other
    /// repository in this product (<c>IBookingCalendarRepository</c>, <c>ICustomerRepository</c>,
    /// <c>IEventRepository</c>...) - one verb for "persist a change to an entity this port already
    /// handed out", everywhere.</summary>
    Task SaveAsync(Operator @operator, CancellationToken cancellationToken);
}
