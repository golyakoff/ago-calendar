namespace Ago.Calendar.Worker;

/// <summary>
/// `22-08`/`adr/0149` rule 1: this product's own copy of the wire shape <c>ago-chat</c>'s
/// <c>Ago.Chat.Contracts.TenantSuspensionChanged</c> publishes - independently declared, never shared,
/// the identical reason <see cref="ModuleQuantityGrantedWireContract"/>'s own remarks give:
/// <c>Ago.Chat.Contracts</c> is not a package this product may reference (`adr/0012`, `adr/0027`).
///
/// <para><see cref="SuspendedUntil"/> is the *lease's* own instant - "now plus the lease length" as
/// chat computed it, never the owner-facing duration a tenant chose. <see langword="null"/> means "not
/// suspended", published immediately on an explicit lift (`adr/0149` rule 1's own "an immediate event
/// that brings the effect forward").</para>
/// </summary>
internal sealed record TenantSuspensionChangedWireContract(
    Guid SiteId, DateTimeOffset? SuspendedUntil, Guid CorrelationId, DateTimeOffset OccurredAt);
