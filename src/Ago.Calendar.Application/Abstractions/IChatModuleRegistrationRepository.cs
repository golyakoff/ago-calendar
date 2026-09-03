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
/// </summary>
public interface IChatModuleRegistrationRepository
{
    Task<ChatModuleRegistration?> GetByTenantIdAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(ChatModuleRegistration registration, CancellationToken cancellationToken);
}
