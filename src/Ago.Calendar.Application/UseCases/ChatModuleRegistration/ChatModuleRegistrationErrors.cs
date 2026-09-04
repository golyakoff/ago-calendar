using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`: this surface's own expected failures - the same <c>&lt;area&gt;.&lt;reason&gt;</c>
/// vocabulary <see cref="Ago.Calendar.Application.UseCases.ChatModuleTask.ChatModuleTaskErrors"/>
/// already established for the sibling chat surface. This one is not operator-facing at all (its only
/// caller is `Ago.Chat.*`'s own server, proven by a provisioning secret before any handler here runs -
/// see <c>ModuleRegistrationEndpoints</c>'s own remarks), so there is no enumeration concern shaping
/// these messages toward vagueness either.
/// </summary>
public static class ChatModuleRegistrationErrors
{
    public static Error TenantNotFound() => new(
        "chat_module_registration.tenant_not_found",
        "No tenant is provisioned at that id - nothing to register a chat module against yet.");

    /// <summary>`22-11`'s own Done-when: enabling is a create, and a second create for a tenant that
    /// already has a row is a caller mistake - the remedy is <c>RotateChatModuleCredentialHandler</c>,
    /// not a second registration silently overwriting the first's grace-window bookkeeping.</summary>
    public static Error AlreadyRegistered() => new(
        "chat_module_registration.already_registered",
        "This tenant already has a chat module registration - rotate it instead of registering again.");

    public static Error NotFound() => new(
        "chat_module_registration.not_found",
        "No chat module registration exists for that tenant.");

    public static Error InvalidCredential(string reason) => new(
        "chat_module_registration.invalid_credential", reason);

    /// <summary>`22-17`: this handler now provisions a missing <see cref="Ago.Calendar.Domain.Tenant"/> rather than
    /// refusing with <see cref="TenantNotFound"/> - see <see cref="RegisterChatModuleHandler"/>'s own
    /// remarks. This is that provisioning step's own failure, kept distinct from
    /// <see cref="InvalidCredential"/> (a different fact was rejected) even though both are
    /// caller-input problems, so a reader of a failure log is not left guessing which value was bad.
    /// Not expected to fire in ordinary operation - <see cref="RegisterChatModule.TenantId"/> is always
    /// a real <c>Guid</c> by construction and a display name has no real validation beyond
    /// non-blank.</summary>
    public static Error TenantProvisioningFailed(string reason) => new(
        "chat_module_registration.tenant_provisioning_failed", reason);
}
