using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-14`/`adr/0100`: which tenants one identity may act in <i>here</i> - the console cannot offer a
/// choice it cannot enumerate, and until this item there was nothing in this product that could
/// answer the question at all.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the same call
/// <see cref="IContactsReadStore"/> and <see cref="IPendingBookingReadStore"/> already made: this
/// returns rows shaped for a switcher - an id and a name - never a <see cref="Tenant"/> aggregate
/// with invariants to enforce. It is also a genuine join (the projection carries the tenancy, the
/// <c>tenants</c> table carries the name), which is exactly the shape
/// <c>ago-chat</c>'s <c>ListMyTenanciesHandler</c> chose to do handler-side with two repositories and
/// an N+1 loop; a single left join costs one round trip instead of one per tenancy, and the read side
/// is where this project already puts hand-written SQL.</para>
///
/// <para><b>The same candidate set <see cref="IRoleAssignmentProjectionStore.ResolveTenantAsync"/>
/// selects from, deliberately.</b> Every projection row for this operator is listed, including one
/// whose permission set is empty (a revocation that has landed). Filtering those out would read
/// better in a dropdown and would be wrong: the switcher would then disagree with the server about
/// what exists, hiding a tenancy that still resolves to a <c>tenant_id</c> claim. What an empty set
/// costs its holder is a per-action <c>403</c> from <see cref="IPermissionChecker"/>, which is the
/// honest thing to show them.</para>
/// </summary>
public interface ITenancyReadStore
{
    Task<IReadOnlyList<TenancyRow>> ListForOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken);
}

/// <param name="TenantName">Empty when this product has no <c>tenants</c> row for a tenancy the
/// projection names - a real, reachable state rather than a defensive one, because the two facts
/// arrive from different places: a grant is replicated by <c>RoleAssignmentsChangedConsumer</c> the
/// moment `ago-chat` publishes it, while the tenant row appears only when the calendar module is
/// provisioned for that account (`22-11`/`22-17`). Listed anyway, with the name the caller can render
/// a fallback for, rather than dropped - a tenancy that resolves must be one the switcher can
/// name.</param>
public readonly record struct TenancyRow(TenantId TenantId, string TenantName);
