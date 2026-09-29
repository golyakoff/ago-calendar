using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>`26-275`/`adr/0189`: the outbox envelope for the calendar-initiated person-erasure
/// cascade - see <see cref="PersonErased"/>'s own remarks. Built by
/// <c>ErasePersonHandler</c> (Application - clean-architecture.md's own placement for the
/// domain-to-contract mapping, the identical split <see cref="PersonRegisteredMapper"/> already
/// establishes) and staged by <c>IPersonEraseStore</c> inside the same transaction as the erasure
/// itself.</summary>
public static class PersonErasedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid personId, TenantId tenantId, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);

        var correlationId = idGenerator.NewId(occurredAt);
        var contract = new PersonErased(personId, tenantId.Value, occurredAt, correlationId);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(PersonErased),
            Version: 1,
            // Per person, the identical reasoning PersonRegisteredMapper's own remarks give: the only
            // ordering that could ever matter is between events about one person, and a tenant-wide
            // key would serialise unrelated erasures for nothing.
            PartitionKey: personId.ToString(),
            OccurredAt: occurredAt,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
