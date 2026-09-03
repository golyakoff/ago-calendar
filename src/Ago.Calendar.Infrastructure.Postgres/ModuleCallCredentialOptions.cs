namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-02`: bound from <c>ChatModule:*</c>, a sibling of
/// <c>Ago.Calendar.Application.UseCases.ChatModuleTask.ChatModuleTaskOptions</c> - the identical section,
/// because both describe the same static, single-deployment chat integration
/// (<c>ChatModuleTaskOptions</c>'s own remarks: "every chat-originated task in this deployment is
/// answered by exactly one tenant's one calendar"). This is that deployment's other half: not which
/// calendar answers, but who is allowed to ask.
/// </summary>
public sealed class ModuleCallCredentialOptions
{
    public const string SectionName = "ChatModule";

    /// <summary>The shared secret this deployment's own `Ago.Chat.*` registration was given - see
    /// <c>Ago.Chat.Domain.ModuleCredential</c>'s own remarks on the other side of this pairing. Empty
    /// by default, which <see cref="HmacModuleCallCredentialValidator"/> treats as "no credential
    /// configured" and therefore refuses <em>any</em> presented credential outright (a validator that
    /// silently accepted every signature when unconfigured would be worse than one that refuses to
    /// start answering module calls at all).</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>`22-02`'s own rollout affordance - see this item's report for the argument. Defaults to
    /// <see langword="true"/> (Done-when's own "an unauthenticated call is refused" is the state this
    /// code ships in); an operator sets this <see langword="false"/> in configuration only for the
    /// narrow window where this host's own image has rolled out before `Ago.Chat.*`'s header-sending
    /// image has, so that window does not become an outage. A <em>wrong</em> credential is refused
    /// regardless of this flag - only a missing one is affected.</summary>
    public bool RequireCredential { get; set; } = true;
}
