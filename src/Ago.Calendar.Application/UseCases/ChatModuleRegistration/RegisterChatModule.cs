namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`: the module-side half of `Ago.Chat.*`'s own `EnableModuleForSite` - the write this item's
/// own backlog item named as never happening outside a test. <see cref="TenantId"/> arrives already
/// proven by <see cref="Abstractions.IModuleProvisioningAuthenticator"/> before this command is ever
/// built (<c>ModuleRegistrationEndpoints</c>'s own remarks), so this handler's only job is the write
/// itself and the one business rule that write has: a tenant gets at most one row this way (see
/// <see cref="RegisterChatModuleHandler"/>).
/// </summary>
public sealed record RegisterChatModule(Guid TenantId, string Credential);
