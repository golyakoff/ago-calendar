namespace Ago.Calendar.Worker;

/// <summary>
/// `23-12`/`adr/0123`: this product's own copy of the wire shape `ago-chat`'s
/// <c>Ago.Chat.Contracts.ContactVisibilityChanged</c> publishes - independently declared, never
/// shared, the identical reasoning <see cref="RoleAssignmentsChangedWireContract"/>'s own remarks give
/// for itself: <c>Ago.Chat.Contracts</c> is not a project this product may reference (adr/0012,
/// adr/0027).
///
/// <para>Property names match the source record exactly - the publisher serialises with
/// <see cref="System.Text.Json.JsonSerializer"/>'s default options (no camelCase policy), so the wire
/// carries PascalCase field names.</para>
///
/// <para><see cref="Rung"/> is <c>Ago.Chat.Domain.ContactVisibility</c>'s own member name, serialised
/// as text - <c>"Visible"</c> or <c>"MaskedWithReveal"</c>, never a third value (that contract's own
/// remarks) - parsed here into this product's own, independently-declared
/// <see cref="Ago.Calendar.Domain.ContactVisibility"/>.</para>
/// </summary>
internal sealed record ContactVisibilityChangedWireContract(
    Guid SiteId, string Rung, Guid CorrelationId, DateTimeOffset OccurredAt);
