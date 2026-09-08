namespace Ago.Calendar.Worker;

/// <summary>
/// `23-59`/`adr/0147`: this product's own copy of the wire shape `ago-chat`'s
/// <c>Ago.Chat.Contracts.ContactCollected</c> publishes - independently declared, never shared, the
/// identical reasoning <see cref="RoleAssignmentsChangedWireContract"/>'s own remarks give in full for
/// itself.
///
/// <para>Property names match the source record exactly - the publisher serialises with
/// <see cref="System.Text.Json.JsonSerializer"/>'s default options (no camelCase policy), so the wire
/// carries PascalCase field names, and this record's own properties are named to match without needing
/// any deserialization option of its own.</para>
/// </summary>
internal sealed record ContactCollectedWireContract(
    Guid ContactDetailId,
    Guid SiteId,
    string Kind,
    string Value,
    DateTimeOffset RecordedAt,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
