using Ago.Platform.Abstractions;

namespace Ago.Calendar.Worker;

/// <summary>
/// `25-44`: the identical shape `Ago.Chat.Worker.ClaimedOutboxRow` already establishes for the same
/// reason - see that type's own remarks for why <see cref="OccurredAt"/> is a raw, UTC-kinded
/// <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/> (Npgsql's own raw-ADO.NET reader
/// shape for a <c>timestamptz</c> column, distinct from EF's provider-level conversion). Restated here
/// rather than shared, for the identical reason `Ago.Chat.Contracts.ChatTracing`'s own remarks give
/// for not extracting a one-field project: `Ago.Chat.Worker` and `Ago.Calendar.Worker` are two
/// separate products' own hosts with no shared project between them below `Ago.Platform.*`, and this
/// type carries no behaviour a platform abstraction would be the right place for - it is host-shaped
/// wiring, not a product-neutral mechanism.
/// </summary>
internal sealed record ClaimedOutboxRow(
    Guid Id,
    DateTime OccurredAt,
    string Type,
    int Version,
    string Payload,
    string PartitionKey,
    Guid CorrelationId,
    int Attempts,
    string? TraceContext)
{
    public EventEnvelope ToEnvelope() => new(
        MessageId: Id,
        Type: Type,
        Version: Version,
        PartitionKey: PartitionKey,
        OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)),
        CorrelationId: CorrelationId,
        Payload: Payload);
}
