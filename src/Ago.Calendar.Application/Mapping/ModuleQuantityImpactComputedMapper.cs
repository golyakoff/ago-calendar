using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>
/// Builds the <see cref="ModuleQuantityImpactComputed"/> envelope - the identical role
/// <see cref="BookingConfirmedMapper"/> plays for its own sibling event. clean-architecture.md: the
/// mapping happens in Application when writing to the outbox, so <see cref="IWorkerQuotaImpactAnswerer"/>
/// (Infrastructure) never has to know the wire shape itself, only the fields that go into it.
/// </summary>
public static class ModuleQuantityImpactComputedMapper
{
    /// <summary>Calendar's own module key - the literal every consumer of this product's own quantity
    /// grant/impact events already compares against
    /// (<c>Ago.Calendar.Worker.ModuleQuantityGrantedConsumer.CalendarModuleKey</c>'s own remarks).
    /// Not a parameter: this mapper only ever builds this product's own reply.</summary>
    private const string CalendarModuleKey = "calendar";

    /// <param name="siteId">`22-03`: the calendar's own <see cref="Domain.TenantId"/>, restated as
    /// chat's own <c>SiteId</c> - the two are the identical value, never converted.</param>
    /// <param name="requestedQuantity">Echoed back from the question, unchanged.</param>
    /// <param name="affectedDisplayNames">Newest-created first - see
    /// <see cref="ModuleQuantityImpactComputed.AffectedItemDisplayNames"/>'s own remarks for why this
    /// order matters.</param>
    /// <param name="correlationId">Threaded through from the question this answers - see the
    /// contract's own remarks on why a fresh id, right for <c>BookingConfirmed</c>'s background sweep,
    /// is wrong here.</param>
    /// <param name="occurredAt">When the calendar computed this answer.</param>
    public static EventEnvelope ToEnvelope(
        Guid siteId,
        int requestedQuantity,
        IReadOnlyList<string> affectedDisplayNames,
        Guid correlationId,
        DateTimeOffset occurredAt,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(affectedDisplayNames);
        ArgumentNullException.ThrowIfNull(idGenerator);

        var contract = new ModuleQuantityImpactComputed(
            SiteId: siteId,
            ModuleKey: CalendarModuleKey,
            RequestedQuantity: requestedQuantity,
            AffectedCount: affectedDisplayNames.Count,
            AffectedItemDisplayNames: affectedDisplayNames,
            CorrelationId: correlationId,
            OccurredAt: occurredAt);

        return new EventEnvelope(
            // A fresh id, not the correlation id: this reply carries no local write of its own to
            // deduplicate against (unlike BookingConfirmed's EventId), and a redelivered
            // ModuleQuantityImpactRequested producing a second reply with a distinct MessageId but
            // the identical values is harmless by design - IModuleQuantityImpactPreviewStore.AnswerAsync
            // on the receiving side is a naturally idempotent overwrite (this item's own report).
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ModuleQuantityImpactComputed),
            Version: 1,
            // Per tenant/site, the same key ModuleQuantityGrantedMapper's own outbound question uses:
            // ordering only matters between successive answers about the same tenant's own module.
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
