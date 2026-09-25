using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>`adr/0184` decision 2: the outbox envelope for a person id this product minted locally
/// for a booking with no chat origin - see <see cref="PersonRegistered"/>'s own remarks.</summary>
public static class PersonRegisteredMapper
{
    public static EventEnvelope ToEnvelope(
        Guid personId, TenantId tenantId, PhoneNumber phone, string? name, DateTimeOffset occurredAt,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);

        var correlationId = idGenerator.NewId(occurredAt);
        var contract = new PersonRegistered(personId, tenantId.Value, phone.Value, name, occurredAt, correlationId);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(PersonRegistered),
            Version: 1,
            // Per person: the only ordering that could ever matter is between events about one
            // person, and a tenant-wide key would serialise unrelated registrations for nothing
            // (messaging.md guarantees order per partition key, never globally).
            PartitionKey: personId.ToString(),
            OccurredAt: occurredAt,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
