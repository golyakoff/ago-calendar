namespace Ago.Calendar.Domain;

/// <summary>
/// `22-04`: this product's own consuming half of adr/0065's registry - "site X has the calendar
/// module enabled, proven by this credential." Before this item, every chat-originated call this
/// deployment ever answered acted on one statically configured tenant
/// (<c>Ago.Calendar.Application.UseCases.ChatModuleTask.ChatModuleTaskOptions</c>, now removed) and
/// was checked against one deployment-wide secret
/// (<c>Ago.Calendar.Infrastructure.Postgres.ModuleCallCredentialOptions</c>, also removed) - adr/0094's
/// own named limit: "whoever holds the raw secret can mint one for any site that deployment serves."
/// This row is what closes both at once: a credential is only ever checked against the one secret its
/// own claimed site registered, and the tenant a call resolves to is the tenant <i>this</i> row names,
/// never a static default.
///
/// <para><b>Keyed by <see cref="TenantId"/>, not by a separate chat-owned site id column.</b>
/// `22-03`/adr/0093 made a calendar tenant's id equal the account id whenever the caller supplies one
/// at registration - <c>RegisterTenantHandler</c>'s own remarks: "AGO Chat calls the same value
/// <c>SiteId</c>." A row here is only ever meaningful for a tenant provisioned that way, so the
/// credential's own claimed site id <i>is</i> a <see cref="TenantId"/> already - a second "site id"
/// column holding the identical <see cref="Guid"/> under a different name would be a value duplicated
/// for no new fact, not a mapping the domain actually needs. A tenant provisioned through the
/// standalone door (`20-06`'s own minted id, adr/0093's door deliberately left open) simply has no row
/// here yet, which is indistinguishable from "chat module not enabled" - correctly, since neither has
/// happened.</para>
///
/// <para><b>No <see cref="CalendarId"/> of its own.</b> Considered and rejected: a chat-originated
/// task needs to answer for exactly one of the tenant's calendars, but which one is resolved at call
/// time from the tenant's own published calendars
/// (<see cref="Ago.Calendar.Application.UseCases.ChatModuleTask.StartModuleTaskHandler"/>'s own
/// remarks) rather than pinned here. Storing it here would mean two places could disagree about which
/// calendar answers chat - this row's own column, and whatever the tenant's console actually
/// published - and the tenant's own calendars are already the one place that fact lives.</para>
///
/// <para><b>A separate entity from <see cref="Tenant"/> itself, not a field on it.</b> The identical
/// reasoning `Ago.Chat.Domain.EnabledModule`'s own remarks give for staying out of <c>Site</c>: this
/// is a fact about one integration channel with its own lifecycle (registered once, rotated
/// independently), not a fact about the account itself, and folding it onto <see cref="Tenant"/> would
/// grow that aggregate's surface for every tenant regardless of whether it ever enables chat.</para>
///
/// <para><b>`22-11`: <see cref="PreviousCredential"/> is the domain's own answer to "rotate without
/// downtime".</b> The moment this row's <see cref="Credential"/> changes, <c>Ago.Chat.*</c> switches to
/// signing every new call with the new value - but a call already in flight, or one signed a moment
/// before the switch and only now arriving, was signed with the old one. Refusing it outright would be
/// the "downtime" this item's own Done-when names as the thing to avoid. So a rotation does not
/// discard the old secret; it demotes it to <see cref="PreviousCredential"/> with its own
/// <see cref="PreviousCredentialExpiresAt"/>, and <see cref="ActiveCredentials"/> is the one place that
/// answers "which secrets currently prove a call for this tenant" - a domain policy (how long a retired
/// secret keeps working) rather than an infrastructure detail, which is why it lives here and not in
/// <c>HmacModuleCallCredentialValidator</c>, which only asks this type the question and iterates
/// whatever it gets back.</para>
/// </summary>
public sealed class ChatModuleRegistration
{
    public TenantId TenantId { get; }

    public ChatModuleCredential Credential { get; }

    /// <summary>`22-11`: the credential a rotation just replaced, kept valid until
    /// <see cref="PreviousCredentialExpiresAt"/> - <see langword="null"/> for a row that has never been
    /// rotated, and for one whose grace window has already been read as expired by <see cref="Rotate"/>
    /// itself (a rotation collapses an already-expired previous credential rather than chaining a
    /// second one - see that method's own remarks).</summary>
    public ChatModuleCredential? PreviousCredential { get; }

    public DateTimeOffset? PreviousCredentialExpiresAt { get; }

    public DateTimeOffset RegisteredAt { get; }

    private ChatModuleRegistration(
        TenantId tenantId, ChatModuleCredential credential, ChatModuleCredential? previousCredential,
        DateTimeOffset? previousCredentialExpiresAt, DateTimeOffset registeredAt)
    {
        TenantId = tenantId;
        Credential = credential;
        PreviousCredential = previousCredential;
        PreviousCredentialExpiresAt = previousCredentialExpiresAt;
        RegisteredAt = registeredAt;
    }

    // EF Core materialization only - never called by domain code.
    private ChatModuleRegistration()
    {
    }

    public static ChatModuleRegistration Register(TenantId tenantId, ChatModuleCredential credential, DateTimeOffset now) =>
        new(tenantId, credential, previousCredential: null, previousCredentialExpiresAt: null, now);

    /// <summary>
    /// `22-11`: replaces <see cref="Credential"/>, keeping the outgoing value valid for
    /// <paramref name="overlapWindow"/> more - the grace period an operator relies on for "rotate
    /// without downtime for the site being rotated", the sharper of the two claims that Done-when
    /// makes (the weaker one - other sites unaffected - already followed for free from every row being
    /// independent, since `22-04`).
    ///
    /// <para><b>Does not chain a second previous credential.</b> If this row already carries a
    /// <see cref="PreviousCredential"/> whose own window has not yet expired, rotating again would
    /// leave three secrets simultaneously valid unless this method special-cased it - so it does not:
    /// only the current <see cref="Credential"/> is ever demoted, and a not-yet-expired previous one is
    /// silently dropped. A second rotation inside one grace window is rare enough (an operator rotating
    /// twice in the same few minutes) that dropping the still-valid-but-superseded value is the honest
    /// choice - the caller asked for a fresh secret, not for two old ones to keep working.</para>
    /// </summary>
    public ChatModuleRegistration Rotate(ChatModuleCredential newCredential, DateTimeOffset now, TimeSpan overlapWindow) =>
        new(TenantId, newCredential, Credential, now + overlapWindow, RegisteredAt);

    /// <summary>Every credential that currently proves a call for this tenant - the current one, plus
    /// the previous one if it was demoted less than its own grace window ago. Order matters to nobody:
    /// <c>HmacModuleCallCredentialValidator</c> tries each until one verifies or none do.</summary>
    public IEnumerable<ChatModuleCredential> ActiveCredentials(DateTimeOffset now)
    {
        yield return Credential;

        if (PreviousCredential is { } previous && PreviousCredentialExpiresAt is { } expiresAt && now < expiresAt)
        {
            yield return previous;
        }
    }
}
