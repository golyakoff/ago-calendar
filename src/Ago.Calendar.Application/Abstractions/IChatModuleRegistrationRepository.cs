using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-04`: the write-side (EF) port for <see cref="ChatModuleRegistration"/> - adr/0004's "EF for
/// writes" half. Also the port <c>HmacModuleCallCredentialValidator</c> reads through on every module
/// call, rather than a second, Dapper-backed read store the way <c>IEnabledModuleReadStore</c> exists
/// beside <c>IEnabledModuleRepository</c> in `ago-chat`: that split earns its keep on a hot path hit by
/// every visitor message on every site with a module enabled, and this lookup runs once per
/// module-task call - `adr/0065`'s own "most steps run at human pace" volume, not that one. The
/// identical call `Ago.Faq.Application.Abstractions.IModuleSiteRegistrationRepository`'s own remarks
/// make for its sibling.
///
/// <para><b>`22-11`: <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> close the "add-and-read
/// only" gap the item this repository's own report named as the load-bearing finding.</b> Before this
/// item, nothing outside a test ever called <see cref="AddAsync"/> either - now
/// <c>RegisterChatModuleHandler</c> does, driven by `Ago.Chat.*`'s own <c>EnableModuleForSite</c>, and
/// <see cref="UpdateAsync"/>/<see cref="DeleteAsync"/> give a leaked credential and an ended
/// registration the remedy the item's own Context section says add-only left them without.</para>
/// </summary>
public interface IChatModuleRegistrationRepository
{
    Task<ChatModuleRegistration?> GetByTenantIdAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(ChatModuleRegistration registration, CancellationToken cancellationToken);

    /// <summary>`22-11`: persists a rotated row - see <see cref="ChatModuleRegistration.Rotate"/>. The
    /// caller passes the already-rotated instance; this port does not decide rotation policy, only
    /// stores its result, the same read/write split every other repository in this codebase keeps.</summary>
    Task UpdateAsync(ChatModuleRegistration registration, CancellationToken cancellationToken);

    /// <summary>`22-11`: revokes a tenant's registration outright - deletion, not a soft "disabled"
    /// flag, because a revoked registration has no state a future call could ever legitimately read
    /// (unlike, say, a cancelled booking, which stays visible in a report). A revoked tenant's next
    /// call finds no row here at all, which <c>HmacModuleCallCredentialValidator</c> already treats
    /// identically to "never registered" - deletion reuses a refusal path rather than adding a new
    /// one.</summary>
    Task DeleteAsync(TenantId tenantId, CancellationToken cancellationToken);
}
