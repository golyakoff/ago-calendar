namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// Bound from <c>ChatModule:*</c>, a sibling of <c>Booking:*</c>
/// (naming-and-structure.md's options-validation rule). Bound, not hard-validated, at startup: unlike
/// <c>Operator:Authority</c>, leaving this unset does not stop the host from booting - it disables
/// one feature (every <c>/api/v1/module-tasks</c> request answers <c>chat_module_task.not_configured</c>),
/// while every other route, including the widget's own booking flow, is unaffected. See
/// <see cref="CalendarId"/>'s own remarks for where the real check happens instead.
///
/// <para><b>The static-wiring decision this item's backlog entry names in as many words</b>: "Calendar
/// is wired statically... the same category as <c>Auth:Keycloak:Authority</c>, changed by editing
/// config and redeploying." This product has no per-site module registry yet (that is `adr/0065`'s
/// own "the registry: one row saying site X has module K enabled" - explicitly out of scope for the
/// Calendar half of this item), so every chat-originated task in this deployment is answered by
/// exactly one tenant's one calendar, named here.</para>
///
/// <para><b>A public key, not a raw <see cref="TenantId"/>.</b> Every existing `PublicBooking` handler
/// this item reuses (<c>GetBookingSurfaceHandler</c>, <c>GetBookableWorkersHandler</c>,
/// <c>GetOpenSlotsHandler</c>) is built around <c>EmbedScopeResolver</c>, which resolves a tenant from
/// its <see cref="TenantPublicKey"/> - never from an id. Configuring a raw <c>TenantId</c> here would
/// need a fourth resolution path bypassing that resolver entirely, which is exactly the shared
/// preamble those three handlers exist to avoid re-deriving per caller.</para>
/// </summary>
public sealed class ChatModuleTaskOptions
{
    public const string SectionName = "ChatModule";

    /// <summary>Which tenant's booking surface this deployment's chat entry point answers for.</summary>
    public string TenantPublicKey { get; set; } = string.Empty;

    /// <summary>Which of that tenant's calendars. Validated against the tenant's actual published
    /// calendars at request time, not merely against "is this a non-empty guid" at startup - a
    /// configured id that turns out to belong to nobody, or to an unpublished calendar, is a
    /// <c>chat_module_task.not_configured</c> rejection rather than a silent 500.</summary>
    public Guid CalendarId { get; set; }
}
