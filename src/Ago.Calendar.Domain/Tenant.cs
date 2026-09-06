namespace Ago.Calendar.Domain;

/// <summary>
/// The account holder - a shop, a salon, a clinic. Structurally what <c>Site</c> is in AGO Chat, and
/// deliberately not that row: the two products own separate databases (adr/0027), so this is a new
/// top-level entity rather than a borrowed one.
///
/// <para>A tenant configures and never participates. It books nothing, confirms nothing and appears
/// in no <see cref="Event"/>; the day-to-day actor is <see cref="Operator"/>, and the person a
/// customer meets is <see cref="Worker"/>. In a one-person business all three are the same human
/// being, which is exactly why they have to be three types - the model must let them coincide
/// without forcing them to.</para>
/// </summary>
public sealed class Tenant
{
    private readonly List<string> _allowedOrigins = [];

    public TenantId Id { get; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// What a shop's own page writes into the embed's <c>&lt;script&gt;</c> tag (`20-06`). See
    /// <see cref="TenantPublicKey"/> for why it is public, chosen rather than generated, and never
    /// something a request may be authorised by.
    /// </summary>
    public TenantPublicKey PublicKey { get; private set; }

    /// <summary>
    /// The page origins allowed to embed this tenant's booking surface - `5-01`'s
    /// <c>Site.AllowedOrigins</c>, adapted from "site" to "tenant" exactly as `20-06` scoped it.
    ///
    /// <para><b>Empty means no browser may embed this tenant, and that is the safe default.</b> A
    /// tenant registered without origins is bookable by a server-side caller and by nobody's page,
    /// which is the failure a shop notices immediately rather than the one nobody notices.</para>
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// `22-17`: <see langword="true"/> only for a row <see cref="AutoProvisionForChatModule"/> wrote -
    /// never set by <see cref="Register"/>, and never changed after construction. Answers a question
    /// this row's own <see cref="Name"/>/<see cref="PublicKey"/> cannot: both are ordinary,
    /// caller-supplied values on <em>every</em> path, including this one, so nothing about their shape
    /// distinguishes a tenant a human registered (the provisioner, `20-27`, or the dev-only route)
    /// from one <c>RegisterChatModuleHandler</c> minted on the strength of the deployment-wide
    /// provisioning secret alone. See that handler's own remarks for why this distinction exists at
    /// all - it is this item's own answer to "a secret that can create rows in a production database
    /// is a different thing from one that can only act on rows that already exist," named rather than
    /// left unauditable.
    /// </summary>
    public bool AutoProvisioned { get; }

    /// <summary>
    /// `22-07`/`adr/0093`: the calendar add-on's own granted number, on the calendar's own tenancy
    /// row - rule 8's answer to "the calendar is sold in chat, enforced in the calendar". Never read
    /// from a cache and never asked of `ago-chat` at write time: <see cref="IWorkerRepository"/>'s
    /// own quota check reads this column inside the same transaction as the worker it may refuse, and
    /// <see cref="GrantWorkerQuota"/> is the only writer, called by the outbox consumer that projects
    /// `ago-chat`'s own grant - never by a request this product serves directly.
    ///
    /// <para><b>Zero until granted</b>, the same "the calendar is an add-on, not a default" default
    /// every tenant row already carries before anyone has ticked the checkbox and paid - a tenant with
    /// no grant yet can configure calendars and services (this column gates only
    /// <see cref="Worker"/> creation) but cannot create the first worker until the grant lands, a
    /// bounded wait rather than a manual step (this item's own report states the bound).</para>
    /// </summary>
    public int WorkerQuota { get; private set; }

    private Tenant(
        TenantId id, string name, TenantPublicKey publicKey, IEnumerable<string> allowedOrigins, DateTimeOffset now,
        bool autoProvisioned)
    {
        Id = id;
        Name = name;
        PublicKey = publicKey;
        CreatedAt = now;
        AutoProvisioned = autoProvisioned;
        WorkerQuota = 0;
        _allowedOrigins.AddRange(allowedOrigins);
    }

    // EF Core materialization only - never called by domain code.
    private Tenant()
    {
    }

    public static Tenant Register(
        TenantId id, string name, TenantPublicKey publicKey, DateTimeOffset now,
        IEnumerable<string>? allowedOrigins = null)
    {
        // clean-architecture.md: "there is no such thing as a validated-somewhere-else entity". The
        // alternative - a FluentValidation rule in the Application layer - would leave the aggregate
        // constructible in a state the aggregate itself calls illegal, which is the whole failure
        // mode this rule exists to prevent.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tenant(id, name.Trim(), publicKey, Normalize(allowedOrigins ?? []), now, autoProvisioned: false);
    }

    /// <summary>
    /// `22-17`: the one caller this exists for - <c>RegisterChatModuleHandler</c>, provisioning a row
    /// for a tenant id `Ago.Chat.*` named but this product had never seen, on the strength of the
    /// deployment-wide provisioning secret alone (`adr/0095`) rather than a human registering a real
    /// account. A separate factory rather than an optional parameter on <see cref="Register"/>: every
    /// other caller of <see cref="Register"/> is a human-initiated write (the provisioner, the dev-only
    /// route) and must never be able to produce an auto-provisioned row by omission - the identical
    /// "a flag nobody else passes is a flag someone eventually passes by accident" reasoning this
    /// project's own owner-handler split (<c>EnableModuleForSiteAsOwnerHandler</c> vs
    /// <c>EnableModuleForSiteHandler</c>, `ago-chat`) already applies to a provenance distinction of
    /// this same shape.
    /// </summary>
    public static Tenant AutoProvisionForChatModule(TenantId id, string name, TenantPublicKey publicKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tenant(id, name.Trim(), publicKey, [], now, autoProvisioned: true);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>
    /// `22-07`: sets the granted number directly - never incremented/decremented, because
    /// `ModuleQuantityGranted` (`ago-chat`'s own contract) carries the *current* number, not a delta,
    /// the identical "a snapshot, not a diff" shape `22-05`'s own `RoleAssignmentsChanged` chose for
    /// the same reason: ordering is only guaranteed per tenant (rule 6), so a consumer that applied
    /// "+2"/"-1" facts out of order could land on the wrong number forever with no way to notice. A
    /// snapshot re-applied twice (an at-least-once redelivery) sets the identical value both times.
    ///
    /// <para>Deactivating whichever workers become the excess is <b>not this method's job</b> - it
    /// spans <see cref="Worker"/>, a different aggregate, so it is the caller's transaction to
    /// coordinate (<see cref="Application.Abstractions.IWorkerQuotaGrantStore"/>), the same
    /// "an aggregate cannot commit another aggregate's own change" boundary
    /// <see cref="CreateWorkerHandler"/>'s own callers already respect for
    /// <see cref="Worker.JoinCalendar"/>.</para>
    /// </summary>
    public void GrantWorkerQuota(int quota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quota);
        WorkerQuota = quota;
    }

    /// <summary>Replaces the whole list rather than adding one entry, because that is what an editor
    /// screen submits and because a set with an add but no remove grows forever.</summary>
    public void SetAllowedOrigins(IEnumerable<string> origins)
    {
        var normalized = Normalize(origins);
        _allowedOrigins.Clear();
        _allowedOrigins.AddRange(normalized);
    }

    /// <summary>
    /// <b>Layer 2 of `5-01`'s two-layer model, expressed where the rule actually lives.</b> The CORS
    /// policy (layer 1) can only answer "does <i>some</i> tenant allow this origin", because a
    /// preflight has not yet said which tenant the request is for. This answers the question that
    /// makes it a tenant boundary: does <i>this</i> tenant allow it. Putting the comparison on the
    /// aggregate rather than in each handler is what keeps the normalisation rules (lowercase scheme
    /// and host, no trailing slash) in one place - three handlers each doing their own string compare
    /// is three chances for one of them to be case-sensitive.
    /// </summary>
    public bool Allows(string origin) =>
        !string.IsNullOrWhiteSpace(origin) && _allowedOrigins.Contains(NormalizeOne(origin), StringComparer.Ordinal);

    private static List<string> Normalize(IEnumerable<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var normalized = new List<string>();
        foreach (var origin in origins)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);

            var value = NormalizeOne(origin);

            // An origin is a scheme, a host and an optional port - never a path, a query or a
            // fragment. Rejecting the longer forms here rather than trimming them is deliberate: a
            // tenant who typed "https://shop.example/booking" believes the path is doing something,
            // and silently storing "https://shop.example" would grant more than they asked for.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || value != uri.GetLeftPart(UriPartial.Authority))
            {
                throw new ArgumentException(
                    $"'{origin}' is not an origin. An origin is scheme://host[:port], with no path.", nameof(origins));
            }

            if (!normalized.Contains(value, StringComparer.Ordinal))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Lowercased and stripped of a trailing slash. A browser sends <c>Origin</c> with a lowercase
    /// scheme and host and no trailing slash already; this normalises what a <i>human</i> types into
    /// the console, so the two can be compared with an ordinal equality rather than with a
    /// case-insensitive compare that would also fold a case-sensitive path if one ever slipped in.
    /// </summary>
    private static string NormalizeOne(string origin) =>
        origin.Trim().TrimEnd('/').ToLowerInvariant();
}
