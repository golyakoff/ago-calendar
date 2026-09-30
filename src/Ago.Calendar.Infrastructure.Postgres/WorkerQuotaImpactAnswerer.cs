using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`23-88`/`adr/0165`'s <see cref="IWorkerQuotaImpactAnswerer"/> adapter - see that interface's
/// own remarks for why this reads with no lock and reuses <see cref="WorkerQuotaPolicy"/> rather than
/// a second "who is excess" rule.</summary>
public sealed class WorkerQuotaImpactAnswerer(
    AgoCalendarDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator) : IWorkerQuotaImpactAnswerer
{
    public async Task AnswerAsync(
        TenantId tenantId, int requestedQuantity, Guid correlationId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedQuantity);

        // No lock, unlike WorkerQuotaGrantStore.ApplyAsync's own SELECT ... FOR UPDATE - this is a
        // courtesy read for a preview nobody has committed to acting on, not a decision this call is
        // making (IWorkerQuotaImpactAnswerer's own remarks).
        var activeWorkers = await db.Workers
            .Where(w => w.TenantId == tenantId && w.IsActive)
            .ToListAsync(cancellationToken);

        // `26-317`: floored the identical way WorkerQuotaGrantStore.ApplyAsync floors the real
        // downgrade, so a preview for a requested quantity below the default floor never threatens a
        // worker the real grant would leave standing. Only the value fed to the policy call is
        // floored - requestedQuantity itself is echoed into the reply below unchanged (this mapper's
        // own remarks: "echoed back from the question, unchanged"), since it answers "what did the
        // owner type", not "what will actually happen".
        var effectiveQuantity = Math.Max(requestedQuantity, Tenant.DefaultWorkerQuota);

        // The identical rule the real downgrade would apply, not a second one - see
        // IWorkerQuotaImpactAnswerer's own remarks for why reusing this one pure function is the
        // explicitly sanctioned exception to keeping the preview and the enforcement path independent.
        var affected = WorkerQuotaPolicy.SelectWorkersToDeactivate(activeWorkers, effectiveQuantity);
        var affectedDisplayNames = affected.Select(worker => worker.DisplayName).ToList();

        outbox.Enqueue(ModuleQuantityImpactComputedMapper.ToEnvelope(
            tenantId.Value, requestedQuantity, affectedDisplayNames, correlationId, now, idGenerator));

        // The only write this call makes - the outbox row itself, so this is the whole transaction
        // (IWorkerQuotaImpactAnswerer.AnswerAsync's own remarks on why there is nothing else for a
        // caller to combine this save with).
        await db.SaveChangesAsync(cancellationToken);
    }
}
