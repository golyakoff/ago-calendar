using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-05`/`adr/0093`: this product's own copy of the account side's role assignments, replicated
/// over the outbox `ago-chat` already publishes to (`RoleAssignmentsChanged`) - what
/// <c>Operator</c>/<c>Role</c>/<c>RoleAssignment</c> and their three tables used to answer, before this
/// item removed them. Application state, not Infrastructure state: the fact "this operator holds these
/// permissions in this tenant" is a thing the application layer reasons about (a permission check, the
/// claims transformation's own tenant resolution), even though the row that carries it is written by a
/// background consumer rather than by any request this product serves directly. The EF-backed adapter
/// lives in <c>Ago.Calendar.Infrastructure.Postgres.RoleAssignmentProjectionStore</c> - this interface
/// is the port the dependency rule requires so that port stays free of Npgsql/EF Core.
///
/// <para><b>No fallback, ever.</b> A missing row means "this subject holds nothing in this tenant",
/// the same refusal-not-guess shape every other resolution in this product already uses (adr/0088's
/// own two refusals). A projection that silently fell back to "assume allowed" or to a stale cached
/// value the moment its row went missing would be a projection that never told anyone it was
/// stale.</para>
/// </summary>
public interface IRoleAssignmentProjectionStore
{
    /// <summary>Every permission string this operator currently holds in this tenant, empty when the
    /// row is missing or was last written as an empty set (a revocation) - never a distinct case from
    /// "held nothing to begin with", because a permission check does not need to tell them apart.
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Which tenant this operator id resolves to - <c>OperatorIdentityClaimsTransformation</c>'s own
    /// question, now that there is no local <c>operators</c> row to read a <c>TenantId</c> column off
    /// of. <see langword="null"/> both when this subject projects to no tenant at all and when it
    /// projects to more than one - the latter is a real, newly-possible shape this item's own report
    /// names (a person can now hold `calendar:configure` on two different accounts, something the old
    /// one-operator-row-per-subject model could not represent), and resolving it by guessing which one
    /// the caller meant would be exactly the cleverness `adr/0088`'s own ambiguous-email refusal already
    /// rejected once for a different ambiguity.
    /// </summary>
    Task<TenantId?> FindTenantIdAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages the projection row this operator+tenant pair should now read as - a full replace, not a
    /// merge, because <c>RoleAssignmentsChanged</c> itself carries the complete current permission set
    /// rather than a delta (that event's own remarks explain why: naturally idempotent under
    /// at-least-once, out-of-order-safe within the per-subject ordering the broker actually
    /// guarantees). Stages only - does not save. The caller (the consumer) commits it together with its
    /// own inbox record through <c>Ago.Platform.Abstractions.IInboxChecker.TryRecordAndSaveAsync</c> on
    /// the same tracked context, the identical "stage here, save there" shape
    /// <c>Ago.Platform.Abstractions.IOutboxWriter.Enqueue</c> already uses on the write side.
    /// </summary>
    Task StageAsync(
        OperatorId operatorId,
        TenantId tenantId,
        string externalSubjectId,
        IReadOnlyList<string> permissions,
        DateTimeOffset asOf,
        CancellationToken cancellationToken);
}
