using System.Security.Claims;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Microsoft.AspNetCore.Authentication;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// Turns "who this person is to Keycloak" into "which tenant this is here, and what may they do".
///
/// <para><b>`22-05`/`adr/0093`: no `operators` table left to resolve against.</b> Before this item, a
/// validated `sub` was looked up in this product's own <c>operators</c> row to learn its
/// <c>OperatorId</c> and <c>TenantId</c>. There is no such row any more - <c>OperatorId</c> is now a
/// deterministic function of the subject itself (<see cref="OperatorId.FromExternalSubjectId"/>), and
/// the tenant is whatever <see cref="IRoleAssignmentProjectionStore.FindTenantIdAsync"/> says that
/// derived id resolves to in the projection <c>RoleAssignmentsChanged</c> replicates from the account
/// side. Same shape adr/0022 established - one lookup, two claims added, before authorization runs -
/// against a different fact than before.</para>
///
/// <para><b>What replaces `adr/0088`'s email-invite linking: nothing calendar-specific, because there
/// is only one invite now.</b> That ADR existed to answer "how does a person become a Calendar
/// operator" when Calendar held its own identity - invited by email, linked on first sign-in. Under
/// `adr/0093` a person's calendar access is just one more permission grant on the account side
/// (`ago-chat`'s own `OperatorInvite`); there is nothing left for a second, calendar-owned invite
/// mechanism to do. A colleague is invited once, on the account side, and if that grant includes
/// `calendar:configure` (or any other calendar permission) the projection carries it here
/// automatically the moment the account side's outbox delivers it - no separate "sign into the
/// calendar console once to link" step, and no `InvitedEmail`/`IsAccountOwner` concept survives this
/// item at all.</para>
///
/// <para><b>Refused, not auto-provisioned - unchanged.</b> A `sub` the projection has never heard of,
/// or one whose projection rows name more than one tenant and whose request named none
/// (<see cref="IRoleAssignmentProjectionStore.ResolveTenantAsync"/>'s own remarks), adds no claim.
/// <see cref="CalendarClaims.OperatorPolicy"/> then refuses the request with a <c>403</c>, the
/// identical shape `authorization.md`'s own "an action from a subject with no authorization is
/// refused, never auto-provisioned" names as the property this projection has to preserve.</para>
///
/// <para><b>`22-14`/`adr/0100`: the caller may now name the tenant, and this is still a
/// server-derived claim.</b> `22-05` made "one subject, two tenants" an ordinary representable state,
/// and the resolution above refuses it - correctly, since there is no honest answer to "which of
/// these two" without asking - which left a real person with a real grant seeing exactly what a person
/// with no grant sees. The console now asks, and sends the answer as
/// <see cref="ActiveSiteHeaderName"/>: `adr/0068`'s existing header, carrying a value that is
/// literally this product's own <c>TenantId</c> (<c>RoleAssignmentsChangedConsumer</c> maps
/// `ago-chat`'s <c>SiteId</c> straight onto it), rather than a second name for the same id.</para>
///
/// <para>The header is a <i>request</i>, never a fact. It is handed to
/// <see cref="IRoleAssignmentProjectionStore.ResolveTenantAsync"/>, whose requested-tenant branch
/// answers only out of this operator's own projection rows - so the claim minted below is still
/// something the database said, in the same read, and `tenant-isolation.md`'s "a server-derived
/// claim" category still describes it. What changed is which of several server-known tenants the
/// claim carries, not who decides whether it may.</para>
///
/// <para><b>A header naming a tenant this operator holds nothing in is refused, not ignored.</b> That
/// is the deliberate difference from `ago-chat`'s otherwise identical transformation, whose own
/// remarks call a malformed signal "no site requested": there, ignoring it can only fail to narrow.
/// Here it would also fail to narrow - straight into the single-tenancy fallback - and a request that
/// asked to act in tenant A must never quietly act in tenant B. Malformed (not a
/// <see cref="Guid"/>) is the one exception and is treated as absent, because a value that cannot
/// name any tenant has not selected one.</para>
///
/// <para><b>Cost, stated the same way adr/0022 stated it:</b> one database read per authenticated
/// request - the same one query <c>PermissionChecker</c> would otherwise pay a moment later anyway,
/// not a new order of magnitude. Not cached, for the same rule-8 reason the projection itself
/// exists: a cached tenant resolution is a cached authorization fact.</para>
///
/// <para><b>Idempotent, because it is not.</b> ASP.NET Core can run an <see cref="IClaimsTransformation"/>
/// more than once per request - a documented sharp edge. The guard below stops a principal
/// accumulating repeated claims.</para>
/// </summary>
public sealed class OperatorIdentityClaimsTransformation(
    IRoleAssignmentProjectionStore projections, IHttpContextAccessor httpContexts)
    : IClaimsTransformation
{
    /// <summary>
    /// `22-14`/`adr/0100`: the header the console already sends on every authenticated call
    /// (`13-07`/`adr/0068`, `ago-console`'s <c>src/api/activeSite.ts</c>). Spelled the same as
    /// `ago-chat`'s <c>OperatorIdentityClaimsTransformation.ActiveSiteHeaderName</c> on purpose - the
    /// value is one id, and a second header name for it would be one more thing that can drift out of
    /// step with the first, for a console that would then have to remember which backend gets which
    /// spelling of the same choice.
    /// </summary>
    public const string ActiveSiteHeaderName = "X-Ago-Active-Site";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(claim => claim.Type == CalendarClaims.OperatorId))
        {
            return principal;
        }

        // `MapInboundClaims = false` (see AuthenticationSetup) keeps the JWT's own claim names, so
        // this is literally the token's `sub` and not the SOAP-era URI .NET would otherwise rename it
        // to. Both spellings are accepted anyway: a principal built by something other than the JWT
        // handler - a test scheme, a future scheme - should not silently fail to resolve.
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return principal;
        }

        var operatorId = OperatorId.FromExternalSubjectId(subject);
        var tenantId = await projections.ResolveTenantAsync(
            operatorId, ReadRequestedTenantId(), CancellationToken.None);
        if (tenantId is null)
        {
            // No match, no claim, and no exception: a real Keycloak user this product's own
            // projection carries no row for - never granted a calendar permission, granted one on
            // more than one tenant with no way to say which was meant here, or (since `22-14`) one
            // who named a tenant they hold nothing in - is refused by the policy, a 403 rather than
            // a 500.
            return principal;
        }

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(CalendarClaims.OperatorId, operatorId.Value.ToString()));
        identity.AddClaim(new Claim(CalendarClaims.TenantId, tenantId.Value.Value.ToString()));

        // A clone, not a mutation of the incoming identity. The principal the authentication handler
        // produced may be cached by the handler itself; adding to it in place would leak this
        // request's claims into whatever else holds the reference.
        var transformed = principal.Clone();
        transformed.AddIdentity(identity);
        return transformed;
    }

    /// <summary>
    /// `22-14`: the tenant this request asked to act in, or <see langword="null"/> for "did not ask".
    ///
    /// <para><b>Header only - no query-string fallback</b>, unlike `ago-chat`'s equivalent. That
    /// fallback exists there for one reason: a browser cannot attach a custom header to a WebSocket
    /// upgrade, and `ago-chat` has a SignalR hub. This product has none (<c>AuthenticationSetup</c>'s
    /// own "one scheme, not two" remarks - there is no visitor and no realtime surface here), so
    /// adding a second place a caller could name a tenant would be a second thing to keep verified
    /// for a client that does not exist.</para>
    ///
    /// <para>A value that is not a <see cref="Guid"/> reads as absent rather than as a refusal: it
    /// names no tenant, so it has selected none, and the ordinary single-tenancy resolution applies.
    /// A well-formed id this operator holds nothing in is a different thing entirely and is refused -
    /// see this class's own remarks, and <see cref="IRoleAssignmentProjectionStore.ResolveTenantAsync"/>.
    /// </para>
    /// </summary>
    private TenantId? ReadRequestedTenantId()
    {
        var raw = httpContexts.HttpContext?.Request.Headers[ActiveSiteHeaderName].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var tenantId)
            ? new TenantId(tenantId)
            : null;
    }
}
