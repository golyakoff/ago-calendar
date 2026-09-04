using System.Security.Claims;
using Ago.Calendar.Api.Auth;
using Ago.Calendar.Application.UseCases.Tenancies;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Api.Me;

/// <summary>
/// `22-14`/`adr/0100`: routes about the calling <i>identity</i> rather than about an already-resolved
/// tenant. There is exactly one, and it is here rather than in
/// <see cref="Configuration.ConsoleEndpoints"/> because it is the one operator-reachable route that
/// cannot carry <see cref="CalendarClaims.OperatorPolicy"/> - an identity with calendar grants on two
/// accounts has no <c>tenant_id</c> claim until it names one, and this is the read that tells it what
/// there is to name.
///
/// <para><b>Still nothing here reads a tenant off the request.</b>
/// <see cref="Configuration.ConsoleEndpoints"/>'s own "the tenant is never in a route, a body or a
/// query string" rule is unchanged for every route it maps; `22-14` added exactly one place a caller
/// may name a tenant - the <c>X-Ago-Active-Site</c> request header - and that value never reaches a
/// handler: it is consumed by <see cref="OperatorIdentityClaimsTransformation"/>, which turns it into
/// a <c>tenant_id</c> claim only after the projection has proved the grant. Every handler still reads
/// the claim.</para>
/// </summary>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/me/tenancies", HandleListMyTenanciesAsync)
            .RequireAuthorization(CalendarClaims.IdentityPolicy)
            .WithName("ListMyTenancies");

        return app;
    }

    private static async Task<IResult> HandleListMyTenanciesAsync(
        ClaimsPrincipal principal,
        ListMyTenanciesHandler handler,
        CancellationToken cancellationToken)
    {
        // Off the validated token's own `sub`, never from the request, and derived here the same way
        // OperatorIdentityClaimsTransformation derives it - `ClaimsPrincipalExtensions.GetOperatorId`
        // is deliberately not usable on this route, because its whole premise is a principal that has
        // already resolved to one tenant.
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            // CalendarClaims.IdentityPolicy already required a validated token, so a token with no
            // subject is a misconfigured realm rather than a caller error - the same call
            // `ago-chat`'s MeEndpoints makes for the identical case.
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var tenancies = await handler.HandleAsync(
            new ListMyTenancies(OperatorId.FromExternalSubjectId(subject)), cancellationToken);

        return Results.Ok(new TenanciesResponse(
            [.. tenancies.Select(tenancy => new TenancyResponse(tenancy.TenantId.Value, tenancy.TenantName))]));
    }
}
