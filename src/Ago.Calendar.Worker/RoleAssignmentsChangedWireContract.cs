namespace Ago.Calendar.Worker;

/// <summary>
/// `22-05`/`adr/0093`: this product's own copy of the wire shape <c>ago-chat</c>'s
/// <c>Ago.Chat.Contracts.RoleAssignmentsChanged</c> publishes - independently declared, never shared,
/// for the same reason `adr/0094` chose two independent validators over a package for the module-task
/// channel: <c>Ago.Chat.Contracts</c> is not published as a NuGet package this product may reference
/// (only <c>Ago.Platform.*</c> ships that way, `adr/0012`), and even if it were, referencing another
/// product's Contracts assembly is exactly the cross-product dependency the repository split exists to
/// prevent (`adr/0027`). This is the twin adr/0094's own duplication comment asks every copy to name -
/// naming its counterpart here rather than leaving the duplication silent.
///
/// <para>Property names match the source record exactly - the publisher serialises with
/// <see cref="System.Text.Json.JsonSerializer"/>'s default options (no camelCase policy), so the wire
/// carries PascalCase field names, and this record's own properties are named to match without needing
/// any deserialization option of its own.</para>
/// </summary>
internal sealed record RoleAssignmentsChangedWireContract(
    string ExternalSubjectId,
    Guid SiteId,
    IReadOnlyList<string> Permissions,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
