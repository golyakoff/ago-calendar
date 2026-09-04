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
    /// Which tenant this request acts in - <c>OperatorIdentityClaimsTransformation</c>'s own question,
    /// now that there is no local <c>operators</c> row to read a <c>TenantId</c> column off of.
    ///
    /// <para><b>`22-14`/`adr/0100`: <paramref name="requestedTenantId"/> is the caller's own choice,
    /// and this method is the only thing that makes it safe.</b> Before this item the signature took
    /// an operator id alone and answered <see langword="null"/> whenever the projection named more
    /// than one tenant - honest, but it meant a person granted calendar permissions on two accounts
    /// got no <c>tenant_id</c> claim, so <c>CalendarClaims.OperatorPolicy</c> refused them and every
    /// calendar screen was simply absent. `22-14` let the console name the tenant instead
    /// (<c>X-Ago-Active-Site</c>, `adr/0068`'s existing header, the same id).</para>
    ///
    /// <list type="bullet">
    /// <item><b>Requested, and this operator holds a projection row in it</b> -&gt; that tenant. The
    /// row's existence <i>is</i> the authorization: the implementation's <c>WHERE</c> clause carries
    /// both the operator id and the requested tenant id, so there is no separate check a later
    /// refactor can drop and no window between "verified" and "used".</item>
    /// <item><b>Requested, and this operator holds nothing in it</b> -&gt; <see langword="null"/>,
    /// <b>never a fallback to one of their real tenancies.</b> A caller-supplied value may only ever
    /// <i>select among</i> rows the query itself proved belong to this operator; falling back would
    /// answer "you asked for A, here is B", which is worse than refusing.</item>
    /// <item><b>Not requested, exactly one row</b> -&gt; that tenant. Byte-for-byte the pre-`22-14`
    /// answer, which is what keeps the one-tenant operator unaffected.</item>
    /// <item><b>Not requested, zero or several rows</b> -&gt; <see langword="null"/>. Unchanged, and
    /// still the refusal `adr/0088`'s ambiguous-email case established: guessing which of two tenants
    /// a request meant is exactly the cleverness this product has already rejected once.</item>
    /// </list>
    /// </summary>
    Task<TenantId?> ResolveTenantAsync(
        OperatorId operatorId, TenantId? requestedTenantId, CancellationToken cancellationToken);

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
