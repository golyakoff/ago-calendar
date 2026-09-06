using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-07`/`adr/0093`: applies `ago-chat`'s own granted worker quota to this product's tenancy row -
/// the consumer half of the crossing this backlog item names ("chat grants; the calendar holds and
/// applies; propagation rides the outbox"). Application state, not merely a repository method on
/// <see cref="ITenantRepository"/>: applying a grant is a decision that spans two aggregates
/// (<see cref="Tenant.GrantWorkerQuota"/> and, when the new number is lower than the tenant's current
/// active headcount, <see cref="Worker.Deactivate"/> on whichever ones
/// <see cref="WorkerQuotaPolicy.SelectWorkersToDeactivate"/> names) - the identical
/// "a use case that touches more than one aggregate gets its own port, not a repository method
/// pretending to be one" reasoning <c>ITenantRepository</c>'s own remarks already give for refusing a
/// bare CRUD shape.
///
/// <para><b>A snapshot, not a delta - the same shape `22-05`'s own
/// <c>IRoleAssignmentProjectionStore.StageAsync</c> chose, for the identical reason.</b> Ordering is
/// only guaranteed per tenant (rule 6, `RoleAssignmentsChanged`'s own <c>PartitionKey</c> precedent
/// this item's own chat-side mapper reuses), so a consumer that applied "+2"/"-1" facts out of order
/// could land on the wrong number forever with no way to notice. <see cref="ApplyAsync"/> instead
/// always sets the *current* number, which makes a redelivery of the identical event a genuine no-op:
/// the tenant row already reads that quota, the active headcount is already at or under it, and
/// nothing further is written.</para>
///
/// <para><b>Deliberately not "stage only" the way <c>IRoleAssignmentProjectionStore.StageAsync</c>
/// is.</b> That method leaves its caller (the consumer) to commit it together with the inbox record in
/// one <c>SaveChangesAsync</c>, which is correct for a trivial single-row replace with no other
/// contender for the row. This operation has a real contender - <c>IWorkerRepository.TryAddWithinQuotaAsync</c>,
/// racing to create the (N+1)-th worker at the exact moment a grant lowers the quota - and closing that
/// race needs a <c>SELECT ... FOR UPDATE</c> lock on the tenant row taken *before* any decision is made,
/// held for the whole read-decide-write sequence. That only works as one self-contained transaction, so
/// <see cref="ApplyAsync"/> commits (or rolls back) its own, the same shape
/// <c>OperatorInviteRedemptionRepository.RedeemAsync</c> and <c>BookingStore.TryBookAsync</c> already
/// use in this codebase for a lock-and-decide sequence with a real contender. The consumer's own inbox
/// record is therefore a second, separate commit - safe only because this method is naturally
/// idempotent on its own (the paragraph above), so a crash between the two commits leaves, at worst, a
/// harmless re-application on redelivery rather than a wrong one. messaging.md's own words: "either
/// defence alone would already be enough."</para>
/// </summary>
public interface IWorkerQuotaGrantStore
{
    /// <summary>
    /// Sets <paramref name="tenantId"/>'s granted worker quota to exactly <paramref name="quota"/>,
    /// deactivating whichever active workers become the excess (<see cref="WorkerQuotaPolicy"/>) when
    /// the new number is lower than the tenant's current active headcount. Raising the quota back up
    /// later does <b>not</b> reactivate anyone this call deactivated - see this item's own report for
    /// why that is a stated, deliberate limitation rather than an oversight.
    /// </summary>
    Task ApplyAsync(TenantId tenantId, int quota, DateTimeOffset now, CancellationToken cancellationToken);
}
