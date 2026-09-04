namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`: the module-side half of `Ago.Chat.*`'s own `EnableModuleForSite` - the write this item's
/// own backlog item named as never happening outside a test. <see cref="TenantId"/> arrives already
/// proven by <see cref="Abstractions.IModuleProvisioningAuthenticator"/> before this command is ever
/// built (<c>ModuleRegistrationEndpoints</c>'s own remarks), so this handler's only job is the write
/// itself and the one business rule that write has: a tenant gets at most one row this way (see
/// <see cref="RegisterChatModuleHandler"/>).
/// </summary>
/// <param name="DisplayName">`22-17`: an opaque, human-readable label for the account being
/// registered - `Ago.Chat.*`'s own <c>Site.Name</c>, carried unopened over the identical generic
/// contract this whole call already rides (<c>IModuleRegistrationGateway.RegisterAsync</c>'s own
/// remarks). Used only when this handler has to provision <see cref="Domain.Tenant"/> itself - see
/// <see cref="RegisterChatModuleHandler"/>'s own remarks for when and why.</param>
public sealed record RegisterChatModule(Guid TenantId, string Credential, string? DisplayName = null);
