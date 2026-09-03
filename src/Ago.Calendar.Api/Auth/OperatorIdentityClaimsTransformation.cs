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
/// or one whose projection rows name more than one tenant (a newly representable shape -
/// <see cref="IRoleAssignmentProjectionStore.FindTenantIdAsync"/>'s own remarks), adds no claim.
/// <see cref="CalendarClaims.OperatorPolicy"/> then refuses the request with a <c>403</c>, the
/// identical shape `authorization.md`'s own "an action from a subject with no authorization is
/// refused, never auto-provisioned" names as the property this projection has to preserve.</para>
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
public sealed class OperatorIdentityClaimsTransformation(IRoleAssignmentProjectionStore projections)
    : IClaimsTransformation
{
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
        var tenantId = await projections.FindTenantIdAsync(operatorId, CancellationToken.None);
        if (tenantId is null)
        {
            // No match, no claim, and no exception: a real Keycloak user this product's own
            // projection carries no row for - never granted a calendar permission, or granted one on
            // more than one tenant with no way to say which was meant here - is refused by the
            // policy, a 403 rather than a 500.
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
}
