namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`'s own fourth Done-when: "the two sides cannot silently disagree: a registration that exists
/// on one side only is detectable." This is the module side's half of that detector - a read, not a
/// write, deliberately answering with no secret in it (<see cref="ChatModuleRegistrationStatus"/>'s own
/// remarks): the caller comparing this against `Ago.Chat.*`'s own `EnabledModule` row does not need the
/// credential's bytes, only whether a row exists and roughly when it last changed.
/// </summary>
public sealed record GetChatModuleRegistrationStatus(Guid TenantId);

/// <param name="Exists">Whether this deployment holds a registration for the tenant at all.</param>
/// <param name="RegisteredAt">When the current row was first created - unset (default) when
/// <paramref name="Exists"/> is <see langword="false"/>.</param>
/// <param name="HasCredentialInGracePeriod">Whether a just-rotated previous credential is still being
/// honoured - a fact worth surfacing to whoever is comparing the two sides, since it means this
/// deployment currently accepts two different secrets for the tenant, not one.</param>
public readonly record struct ChatModuleRegistrationStatus(
    bool Exists, DateTimeOffset RegisteredAt, bool HasCredentialInGracePeriod);
