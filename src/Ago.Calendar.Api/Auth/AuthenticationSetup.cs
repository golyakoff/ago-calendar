using System.Security.Claims;
using Ago.Calendar.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// adr/0022's token flow, this product's own copy (adr/0027).
///
/// <para><b>One scheme, not two.</b> AGO Chat carries a Visitor scheme beside its Operator one
/// because a visitor holds a self-issued token. This product has no visitor scheme and must not grow
/// one: a customer books with a phone number and no account, and `20-03`'s endpoint is
/// unauthenticated on purpose. Everything authenticated here is an operator.</para>
///
/// <para><b>No token exchange</b>, exactly as adr/0022 decided: this host validates Keycloak's own
/// access token and never mints a replacement. What Keycloak proves is <c>sub</c>; what turns that
/// into an operator of a tenant is <see cref="OperatorIdentityClaimsTransformation"/>, in this
/// product's own database.</para>
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>Where the realm lives. Required, and it fails loudly at startup rather than at the
    /// first request: a host that starts and then rejects every token looks like an auth bug for as
    /// long as it takes somebody to read the configuration.</summary>
    public const string AuthoritySetting = "Operator:Authority";

    /// <summary>Optional. Keycloak's <c>aud</c> for a public SPA client is not the client id, so an
    /// audience check is opt-in rather than assumed - configuring one that never matches would reject
    /// every real token, and configuring none is what the realm's own defaults produce.</summary>
    public const string AudienceSetting = "Operator:Audience";

    /// <summary>Development only, and it is why it is not the default: Keycloak in the compose loop
    /// is served over plain HTTP.</summary>
    public const string RequireHttpsMetadataSetting = "Operator:RequireHttpsMetadata";

    public static IServiceCollection AddCalendarOperatorAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = configuration[AuthoritySetting]
            ?? throw new InvalidOperationException(
                $"Set {AuthoritySetting} - the Keycloak realm this console's operators sign in to " +
                "(adr/0022). There is no fallback and no dev stub: `1-06`'s equivalent in AGO Chat was " +
                "deleted outright rather than evolved, and re-inventing it here would re-introduce the " +
                "trust model that ADR removed.");

        var audience = configuration[AudienceSetting];
        var requireHttps = configuration.GetValue(RequireHttpsMetadataSetting, defaultValue: true);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // JWKS discovery and signature validation come from Keycloak directly - no local
                // signing key exists in this product at all, which is the property that makes "there
                // is no self-issued operator token here" checkable rather than merely intended.
                options.Authority = authority;
                options.RequireHttpsMetadata = requireHttps;

                // The token's own claim names, unrenamed. Without this, .NET maps `sub` to a
                // SOAP-era URI and the transformation below reads a claim that is no longer there -
                // adr/0022 names this setting explicitly for that reason.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = audience is not null,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",

                    // Zero, not the five-minute default. adr/0011's stake in this product is sharper
                    // than usual: everything here reasons about instants, and five minutes of
                    // accepted-after-expiry is five minutes a revoked operator keeps working.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                CalendarClaims.OperatorPolicy,
                policy => policy.RequireClaim(CalendarClaims.OperatorId).RequireClaim(CalendarClaims.TenantId));

            // `22-14`: deliberately weaker, and there is exactly one route on it - `GET
            // /api/v1/me/tenancies`, the read that answers "which tenants may I act in". It cannot
            // carry OperatorPolicy: an identity with several calendar tenancies has no resolved
            // `tenant_id` claim until it names one, so the stricter policy would refuse the very call
            // meant to find out what there is to name. Same shape, same reason, as `ago-chat`'s own
            // `RequireKeycloakIdentity` on its `/api/v1/me/tenancies`.
            options.AddPolicy(
                CalendarClaims.IdentityPolicy,
                policy => policy.RequireAuthenticatedUser());
        });

        // `22-14`/`adr/0100`: OperatorIdentityClaimsTransformation reads the request's own
        // `X-Ago-Active-Site` header, and an IClaimsTransformation is handed a principal and nothing
        // else. Registered here rather than in CalendarModule because it is this host's concern -
        // Ago.Calendar.Worker loads the same module and serves no requests at all.
        services.AddHttpContextAccessor();

        // Registered once, runs after validation on every authenticated request. Singleton is safe
        // because it holds no state; its dependency is resolved per call through the request scope's
        // own provider, which is how ASP.NET Core resolves an IClaimsTransformation - hence the
        // scoped registration below rather than a singleton one.
        services.AddScoped<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        return services;
    }
}

/// <summary>
/// Reads what <see cref="OperatorIdentityClaimsTransformation"/> wrote.
///
/// <para><b>Throws rather than returning null.</b> Every caller sits behind
/// <see cref="CalendarClaims.OperatorPolicy"/>, which refuses a principal without both claims - so a
/// missing claim here is not a request that arrived badly, it is a route that lost its policy. A
/// nullable return would let that ship as a silent "acted as nobody"; an exception makes it a 500
/// with a stack trace pointing at the endpoint that forgot.</para>
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static OperatorId GetOperatorId(this ClaimsPrincipal principal) =>
        new(Read(principal, CalendarClaims.OperatorId));

    public static TenantId GetTenantId(this ClaimsPrincipal principal) =>
        new(Read(principal, CalendarClaims.TenantId));

    private static Guid Read(ClaimsPrincipal principal, string claimType)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirstValue(claimType)
            ?? throw new InvalidOperationException(
                $"The principal carries no '{claimType}' claim. Every endpoint reading it must require " +
                $"the '{CalendarClaims.OperatorPolicy}' policy.");

        return Guid.Parse(value);
    }
}
