using Ago.Calendar.Application.UseCases.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Ago.Calendar.Api.Cors;

/// <summary>
/// <b>Layer 1 of `5-01`'s two-layer model.</b> Allows an <c>Origin</c> that <i>any</i> tenant lists,
/// and nothing more - see <see cref="CheckTenantOriginHandler"/> and
/// <c>ITenantRepository.AnyAllowsOriginAsync</c> for why that is the most a CORS policy can ask, and
/// <c>OriginPolicy</c> for the check that makes it a tenant boundary.
///
/// <para><b>In the app, never at the ingress</b> - <c>edge.md</c>'s "What the edge must not be
/// responsible for" is explicit that CORS driven by a tenant's own allowed-origins list belongs here,
/// and AGO Chat's `5-01` set the precedent. An NGINX annotation could not consult the database at
/// all, so it could only be a wildcard, which is the one thing this must never be.</para>
///
/// <para><b>Returns <c>null</c> rather than a deny policy for an unknown origin.</b> A null policy
/// means the CORS middleware writes no <c>Access-Control-Allow-Origin</c> header at all, which is
/// what a browser needs to see to refuse the response. A policy that allowed nothing would still
/// mark the response as CORS-processed and is a subtler way of saying the same thing less
/// reliably.</para>
///
/// <para><b>Two sources of origins, deliberately not merged into one list.</b> A tenant's origins are
/// data, editable by that tenant, and unlock the <i>public</i> surface only. The console's own origin
/// is configuration, editable by whoever deploys this product, and unlocks the <i>authenticated</i>
/// surface. Keeping them apart is what stops a tenant from putting the console's origin in their own
/// list - which would be a tenant granting itself a policy it does not own - and what stops the
/// console's origin from having to exist in every tenant's row.</para>
/// </summary>
public sealed class TenantOriginCorsPolicyProvider(
    IServiceScopeFactory scopeFactory, ConsoleOrigins consoleOrigins) : ICorsPolicyProvider
{
    /// <summary>
    /// How long a browser may reuse one preflight answer. It exists because layer 1 costs a query,
    /// and a browser re-asking on every request would make the "no cache" decision in
    /// <see cref="CheckTenantOriginHandler"/> more expensive than it needs to be. Ten minutes rather
    /// than the maximum: it also bounds how long a *revoked* origin keeps a preflight that already
    /// succeeded, and layer 2 refuses those requests on the server regardless.
    /// </summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(10);

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        ArgumentNullException.ThrowIfNull(context);

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        if (consoleOrigins.Contains(origin))
        {
            return new CorsPolicyBuilder()
                .WithOrigins(origin)
                .WithMethods("GET", "POST", "PUT", "OPTIONS")
                // The console sends adr/0022's bearer token. Note what is still absent:
                // AllowCredentials, because a bearer header is not a credential in CORS's sense and
                // nothing here uses a cookie - so the browser never attaches ambient authority.
                .WithHeaders("Content-Type", "Authorization")
                .SetPreflightMaxAge(PreflightMaxAge)
                .Build();
        }

        // Its own scope: this provider is a singleton, but the repository behind the handler holds a
        // DbContext, which is scoped. Resolving it from the root provider would be a captive
        // dependency - a DbContext living as long as the process.
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CheckTenantOriginHandler>();

        var allowed = await handler.HandleAsync(new CheckTenantOrigin(origin), context.RequestAborted);
        if (!allowed)
        {
            return null;
        }

        return new CorsPolicyBuilder()
            // The exact origin, echoed back - never a wildcard. api-design.md: "CORS is per-site,
            // driven by allowed_origins from the database, never a wildcard".
            .WithOrigins(origin)
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("Content-Type")
            .SetPreflightMaxAge(PreflightMaxAge)
            .Build();
    }
}

/// <summary>
/// The origins this product's own console is served from - configuration, not tenant data. See
/// <see cref="TenantOriginCorsPolicyProvider"/> for why the two lists never merge.
///
/// <para>Empty is the correct default and means "no browser-hosted console talks to this API
/// cross-origin", which is exactly right for a deployment that serves the console and the API from
/// one origin through the gateway (<c>edge.md</c>). It only needs a value where the two are on
/// different origins - a developer's machine, most obviously.</para>
/// </summary>
public sealed class ConsoleOrigins
{
    public const string SectionKey = "Operator:ConsoleOrigins";

    private readonly HashSet<string> _origins;

    public ConsoleOrigins(IEnumerable<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        _origins = new HashSet<string>(
            origins.Select(Normalize).Where(origin => origin.Length > 0), StringComparer.Ordinal);
    }

    public bool Contains(string origin) => _origins.Contains(Normalize(origin));

    /// <summary>The same normalisation <c>Tenant</c> applies to its own list, restated rather than
    /// shared because this side is configuration and importing a Domain method here would be the
    /// host reaching into the aggregate for a string helper.</summary>
    private static string Normalize(string origin) =>
        (origin ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
}
