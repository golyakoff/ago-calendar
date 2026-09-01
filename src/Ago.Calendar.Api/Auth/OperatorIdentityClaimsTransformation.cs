using System.Security.Claims;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Microsoft.AspNetCore.Authentication;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// Turns "who this person is to Keycloak" into "which operator of which tenant this is here".
///
/// <para><b>adr/0022's mechanism, copied rather than shared - and this is the first time that copy
/// actually exists.</b> The 2026-08-26 boundary review recorded, as something it could not establish,
/// that "the duplication adr/0027 explicitly accepted - a copied
/// <c>OperatorIdentityClaimsTransformation</c> - has not happened yet", so the ADR's central cost was
/// still theoretical. It is not any more. What is genuinely copied is the *shape*: a validated
/// principal's <c>sub</c>, one indexed lookup, two claims added. What is not copied is anything the
/// lookup touches - a different table, in a different database, with a different id type and no
/// concept of a site.</para>
///
/// <para><b>Why a claims transformation rather than reading <c>sub</c> at each call site.</b> The
/// alternative would spread "and here is how you turn a subject into an operator" across every
/// endpoint, and the first one to forget the tenant half is a cross-tenant bug. Doing it once, before
/// authorization runs, is also what lets <see cref="CalendarClaims.OperatorPolicy"/> refuse an
/// unknown subject cleanly instead of letting it reach a handler.</para>
///
/// <para><b>Cost, stated the same way adr/0022 stated it:</b> one database read per authenticated
/// request. Not cached - <c>PermissionChecker</c> already pays a lookup on the same request path, so
/// this is not a new order of magnitude, and caching an identity mapping before <c>nfr.md</c> has a
/// number would be optimising a guess.</para>
///
/// <para><b>Idempotent, because it is not.</b> ASP.NET Core runs an <see cref="IClaimsTransformation"/>
/// on every authentication, and on some paths more than once per request - a documented sharp edge.
/// The guard below is what stops a principal accumulating three copies of the same claim; the
/// transformation is otherwise a pure function of <c>sub</c> (and, on the one fallback path below, of
/// <c>sub</c> and the token's own <c>email</c> claim together).</para>
///
/// <para><b>`adr/0088`'s email fallback lives here, not as a second call site.</b> When no operator
/// resolves by <c>sub</c>, this method tries once more against invited rows by email before giving up -
/// <see cref="TryLinkInvitedOperatorByEmailAsync"/>. This is deliberately the only place that
/// mutates an operator as a side effect of an incoming request: `20-08`'s own Done-when draws the line
/// at booking actions creating or mutating a row, not at this transformation completing a deliberate
/// invite the tenant already made. See that method's own remarks for the collision cases it refuses to
/// guess through.</para>
/// </summary>
public sealed class OperatorIdentityClaimsTransformation(IOperatorRepository operators) : IClaimsTransformation
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

        var @operator = await operators.FindByExternalSubjectIdAsync(subject, CancellationToken.None)
            ?? await TryLinkInvitedOperatorByEmailAsync(subject, principal);

        if (@operator is null)
        {
            // No match, no claim, and no exception: a real Keycloak user who is not an operator here -
            // invited or otherwise - is refused by the policy, which is a 403 rather than a 500.
            // adr/0022 chose this explicitly over letting a downstream accessor throw on a missing
            // claim; `20-08`'s own Done-when is the same call made a second time, for a chat-originated
            // action arriving from a subject with no operator row at all.
            return principal;
        }

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(CalendarClaims.OperatorId, @operator.Id.Value.ToString()));
        identity.AddClaim(new Claim(CalendarClaims.TenantId, @operator.TenantId.Value.ToString()));

        // A clone, not a mutation of the incoming identity. The principal the authentication handler
        // produced may be cached by the handler itself; adding to it in place would leak this
        // request's claims into whatever else holds the reference.
        var transformed = principal.Clone();
        transformed.AddIdentity(identity);
        return transformed;
    }

    /// <summary>
    /// `adr/0088`'s invite-a-colleague flow, completed. Called only when <c>sub</c> resolved nothing -
    /// a person who already has an operator row never reaches here, so a subject once bound can never
    /// be re-bound to a different row by an email collision: the direct <c>sub</c> lookup always wins
    /// first, every time, for every request.
    ///
    /// <para><b>The two collisions this deliberately refuses rather than resolves cleverly:</b> two
    /// invited rows sharing an address - <c>FindInvitedByEmailAsync</c> itself returns null for more
    /// than one candidate, so this method never even sees which one to pick; and an invited email that
    /// happens to equal an already-active operator's own (stale) <c>InvitedEmail</c> - impossible to
    /// match here by construction, because the repository query filters to
    /// <c>ExternalSubjectId == null</c>, and an active operator's is never null. Both leave the request
    /// unresolved rather than guessing, exactly as adr/0088's consequence section chose.</para>
    /// </summary>
    private async Task<Operator?> TryLinkInvitedOperatorByEmailAsync(string subject, ClaimsPrincipal principal)
    {
        var emailClaim = principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(emailClaim))
        {
            return null;
        }

        InvitedEmail email;
        try
        {
            email = new InvitedEmail(emailClaim);
        }
        catch (ArgumentException)
        {
            // A token whose `email` claim is not shaped like an address at all - no fallback is
            // possible, and rejecting the token outright is not this transformation's call to make.
            return null;
        }

        var invited = await operators.FindInvitedByEmailAsync(email, CancellationToken.None);
        if (invited is null)
        {
            return null;
        }

        // The one deliberate write in this class - see the type's own remarks on why this is not the
        // "acting on a booking creates a row" failure mode `20-08`'s Done-when forbids: the row already
        // existed, created by a deliberate invite, and this only ever attaches a subject to an
        // already-invited row - never creates one.
        invited.LinkExternalIdentity(subject);
        await operators.SaveAsync(invited, CancellationToken.None);
        return invited;
    }
}
