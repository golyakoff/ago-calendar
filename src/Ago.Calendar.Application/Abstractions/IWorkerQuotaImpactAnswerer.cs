using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-88`/`adr/0165`: answers `ago-chat`'s own <c>ModuleQuantityImpactRequested</c> - the calendar's
/// own half of the async worker-quota impact preview
/// (<c>Ago.Calendar.Worker.ModuleQuantityImpactRequestedConsumer</c>'s own caller). A use case that
/// spans a read of one aggregate (<see cref="Worker"/>) and a publish to a second product's own
/// consumer, so it gets its own port rather than a method bolted onto <see cref="IWorkerRepository"/>
/// - the identical "a use case touching more than a bare CRUD shape gets its own port" reasoning
/// <see cref="IWorkerQuotaGrantStore"/>'s own remarks already give for refusing that shape.
///
/// <para><b>Deliberately not <see cref="IWorkerQuotaGrantStore"/>'s own locked
/// read-decide-write.</b> Nothing here is a decision the calendar is committing to - the owner may
/// never confirm, and <see cref="IWorkerQuotaGrantStore.ApplyAsync"/>'s own transaction recomputes the
/// real answer fresh, inside its own lock, at the later moment a grant is actually applied (CLAUDE.md
/// rule 8: a write decision never trusts a cached read, and this reply is exactly such a cache, by
/// design, on the asking side). So this reads the tenant's own active workers with no lock at all -
/// the answer can go stale the instant after it is computed, and that is fine, because nothing here
/// ever writes on the strength of it.</para>
///
/// <para><b>Reuses <see cref="WorkerQuotaPolicy.SelectWorkersToDeactivate"/> rather than
/// re-deriving the same rule.</b> The whole point of a preview is that it names the same workers, in
/// the same order, that a confirmed downgrade would actually deactivate - a second, independently
/// written "who is excess" rule would drift from the first the moment either one changed, and a
/// preview that disagrees with the real enforcement is worse than no preview at all. This is the one,
/// explicitly sanctioned exception to keeping the preview and the enforcement path independent
/// (`23-88`'s own Outcome section): a trivial, pure, already-tested function call, not a shared
/// transaction, lock or store.</para>
///
/// <para>No idempotency ledger on this side either - see
/// <c>ModuleQuantityImpactRequestedConsumer</c>'s own remarks for why a redelivered question is safe
/// to answer twice.</para>
/// </summary>
public interface IWorkerQuotaImpactAnswerer
{
    /// <summary>
    /// Reads <paramref name="tenantId"/>'s own active workers, computes how many exceed
    /// <paramref name="requestedQuantity"/> and their display names, and stages the reply on this
    /// product's own outbox (<c>ModuleQuantityImpactComputed</c>) - committed by this call, the same
    /// "this method's own name says it saves" contract <see cref="IWorkerQuotaGrantStore.ApplyAsync"/>
    /// already establishes, because there is no caller-owned transaction for this to join: unlike that
    /// method, this one changes no state of its own, so the outbox row is the entire write and there
    /// is nothing else for a caller to combine it with.
    /// </summary>
    /// <param name="tenantId">`22-03`: the calendar's own tenancy row, equal to chat's own
    /// <c>SiteId</c>.</param>
    /// <param name="requestedQuantity">The candidate quantity the owner is considering - answered
    /// against a number that may never actually be granted.</param>
    /// <param name="correlationId">Threaded through from the question this answers - see
    /// <c>ModuleQuantityImpactComputedMapper</c>'s own remarks.</param>
    /// <param name="now">When this answer is computed - <c>OccurredAt</c> on the reply, never the
    /// question's own timestamp.</param>
    Task AnswerAsync(
        TenantId tenantId,
        int requestedQuantity,
        Guid correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
