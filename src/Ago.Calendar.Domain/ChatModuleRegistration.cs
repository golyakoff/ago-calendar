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
/// </summary>
public sealed class ChatModuleRegistration
{
    public TenantId TenantId { get; }

    public ChatModuleCredential Credential { get; }

    public DateTimeOffset RegisteredAt { get; }

    private ChatModuleRegistration(TenantId tenantId, ChatModuleCredential credential, DateTimeOffset registeredAt)
    {
        TenantId = tenantId;
        Credential = credential;
        RegisteredAt = registeredAt;
    }

    // EF Core materialization only - never called by domain code.
    private ChatModuleRegistration()
    {
    }

    public static ChatModuleRegistration Register(TenantId tenantId, ChatModuleCredential credential, DateTimeOffset now) =>
        new(tenantId, credential, now);
}
