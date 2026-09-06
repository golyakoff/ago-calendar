namespace Ago.Calendar.Worker;

/// <summary>
/// `22-07`/`adr/0093`: this product's own copy of the wire shape <c>ago-chat</c>'s
/// <c>Ago.Chat.Contracts.ModuleQuantityGranted</c> publishes - independently declared, never shared,
/// for the identical reason <see cref="RoleAssignmentsChangedWireContract"/>'s own remarks give:
/// <c>Ago.Chat.Contracts</c> is not a package this product may reference (only <c>Ago.Platform.*</c>
/// ships that way, `adr/0012`), and referencing another product's Contracts assembly directly is
/// exactly the cross-product dependency the repository split exists to prevent (`adr/0027`).
///
/// <para>Property names match the source record exactly, for the same default-<c>JsonSerializer</c>-
/// options reason <see cref="RoleAssignmentsChangedWireContract"/> states.</para>
///
/// <para><see cref="ModuleKey"/> is opaque, ago-chat's own `Ago.Chat.Domain.ModuleKey` restated as a
/// plain string here - this product is the one place allowed to know that the value <c>"calendar"</c>
/// means this product, since <c>Ago.Calendar.Worker</c> is calendar's own code, not
/// <c>Ago.Chat.*</c>. <see cref="ModuleQuantityGrantedConsumer"/> is what compares it.</para>
/// </summary>
internal sealed record ModuleQuantityGrantedWireContract(
    Guid SiteId,
    string ModuleKey,
    int Quantity,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
