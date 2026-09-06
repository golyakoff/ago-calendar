using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-12`/`adr/0123`: this product's own copy of the account side's contact-visibility rung,
/// replicated over the outbox `ago-chat` already publishes to (<c>ContactVisibilityChanged</c>) - the
/// identical "a fact this product does not own, projected from an event, read like any other
/// Application state" shape <see cref="IRoleAssignmentProjectionStore"/> already established for the
/// role catalogue. Application state, not Infrastructure state, for the same reason that port's own
/// remarks give: a permission check and a read-model handler both reason about "what rung is this
/// tenant on" directly, even though the row is written by a background consumer. The EF-backed adapter
/// lives in <c>Ago.Calendar.Infrastructure.Postgres.ContactVisibilityProjectionStore</c>.
///
/// <para><b>Keyed on <see cref="TenantId"/> alone, unlike <see cref="IRoleAssignmentProjectionStore"/>'s
/// operator+tenant pair.</b> The rung is a fact about the tenant, not about who is asking - every
/// operator in a tenant sees the identical rung, so there is exactly one row to stage per tenant and
/// no second dimension to key it on.</para>
///
/// <para><b>A missing row means <see cref="ContactVisibility.Visible"/>, never a stricter default.</b>
/// `23-12`'s own Scope, restated as a port contract: the projection can legitimately lag or be absent
/// for a tenant older than `23-11`'s own event, exactly as <see cref="ITenancyReadStore"/>'s own
/// remarks record for a tenancy whose <c>tenants</c> row has not appeared yet. Defaulting to masked
/// would break every existing tenant on the strength of a message that had not arrived - the same
/// "no fallback that invents a stricter answer than the data actually proves" discipline
/// <see cref="IRoleAssignmentProjectionStore"/>'s own remarks state for a missing permission row,
/// applied here to the opposite direction (a missing row there means "holds nothing"; here it means
/// "nothing has ever restricted this tenant").</para>
/// </summary>
public interface IContactVisibilityProjectionStore
{
    Task<ContactVisibility> GetAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages the rung this tenant should now read as - a full replace, never a merge, because
    /// <c>ContactVisibilityChanged</c> itself carries the complete current value rather than a delta
    /// (that contract's own remarks, and the identical discipline <c>RoleAssignmentsChanged</c> states
    /// for itself: naturally idempotent under at-least-once redelivery). Stages only - does not save.
    /// The caller (the consumer) commits it, the same "stage here, save there" shape
    /// <see cref="IRoleAssignmentProjectionStore.StageAsync"/> already uses.
    ///
    /// <para><b>No lock, unlike <see cref="IWorkerQuotaGrantStore.ApplyAsync"/>.</b> A worker quota
    /// grant reads its own current value to decide what to deactivate, so it needs a read-decide-write
    /// held under a lock across one transaction. A rung is displayed, never counted against or
    /// compared - nothing here reads the old value to decide the new one, so there is no
    /// read-decide-write to protect and this can commit in the same single save as the consumer's own
    /// inbox record, the identical one-save shape <see cref="IRoleAssignmentProjectionStore.StageAsync"/>
    /// already uses for the same reason.</para>
    /// </summary>
    Task StageAsync(
        TenantId tenantId, ContactVisibility rung, DateTimeOffset asOf, CancellationToken cancellationToken);
}
