namespace Ago.Calendar.Worker;

/// <summary>
/// `23-88`/`adr/0165`: this product's own copy of the wire shape <c>ago-chat</c>'s own
/// <c>Ago.Chat.Contracts.ModuleQuantityImpactRequested</c> publishes - independently declared, never
/// shared, the identical reason <see cref="ModuleQuantityGrantedWireContract"/>'s own remarks give:
/// <c>Ago.Chat.Contracts</c> is not a package this product may reference (only <c>Ago.Platform.*</c>
/// ships that way, `adr/0012`), and referencing another product's Contracts assembly directly is
/// exactly the cross-product dependency the repository split exists to prevent (`adr/0027`).
///
/// <para>Property names match the source record exactly, for the same default-<c>JsonSerializer</c>-
/// options reason <see cref="ModuleQuantityGrantedWireContract"/> states.</para>
///
/// <para><see cref="ModuleKey"/> is opaque, ago-chat's own <c>Ago.Chat.Domain.ModuleKey</c> restated
/// as a plain string here - this product is the one place allowed to know that the value
/// <c>"calendar"</c> means this product. <see cref="ModuleQuantityImpactRequestedConsumer"/> is what
/// compares it.</para>
/// </summary>
/// <param name="RequestedQuantity">The candidate quantity the owner is considering, not the currently
/// granted one - chat's own remarks on why this is answered against a number that may never actually
/// be granted.</param>
internal sealed record ModuleQuantityImpactRequestedWireContract(
    Guid SiteId,
    string ModuleKey,
    int RequestedQuantity,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
