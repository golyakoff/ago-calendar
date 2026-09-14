using System.Reflection;
using System.Text.RegularExpressions;
using Ago.Calendar.Api.Hubs;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Calendar.Api.Owner;

/// <summary>
/// `24-17`: `GET /api/v1/owner/tenant-isolation` - this product's own analogue of `ago-chat`'s
/// `OwnerTenantIsolationEndpoints`, computed from this process's own `Ago.Calendar.Application.dll`
/// and route table.
///
/// <para><b>Gated by `RequirePlatformOwner`</b> - the first use of that policy anywhere in this
/// product (see `PlatformOwnerRealmRole`'s own remarks on why this is the concept's first crossing
/// into `ago-calendar`, not a pre-existing mechanism). Shown to the platform owner and nobody else,
/// for the identical reason `ago-chat`'s own endpoint is: a count of ungated entry points is "a hint
/// for somebody looking for a way in" (the backlog item's own words) whichever product it describes.</para>
/// </summary>
public static class OwnerTenantScopeEndpoints
{
    private static readonly string[] ExcludedPaths = ["/healthz/version", "/healthz/live", "/healthz/ready"];

    private static readonly Regex ClientSuppliedTenantIdSegment =
        new(@"\{tenantId(:[a-z]+)?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapOwnerTenantScopeEndpoint(this WebApplication app)
    {
        app.MapGet("/api/v1/owner/tenant-isolation", HandleGetSummaryAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleGetSummaryAsync(
        ITenantScopeInspector inspector,
        EndpointDataSource endpoints,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var snapshot = await inspector.GetSnapshotAsync(cancellationToken);
        var (routesAndHubMethods, clientSupplied) = RoutesAndHubMethods(endpoints);

        var response = new TenantScopeSummaryResponse(
            snapshot.EntryPoints,
            snapshot.HandlerClasses,
            snapshot.RbacGated,
            snapshot.NotGated,
            snapshot.NotGatedKeys,
            routesAndHubMethods,
            clientSupplied,
            clock.UtcNow);

        return Results.Ok(response);
    }

    /// <summary>The routes half - read from this process's own live route table plus reflection over
    /// <see cref="CalendarOperatorHub"/>, the same mechanism `ago-chat`'s own
    /// `OwnerTenantIsolationEndpoints.RoutesAndHubMethods` uses and for the identical reason (a
    /// hosting-layer fact `Ago.Calendar.Application` must never know, so it stays here rather than
    /// behind a port). Three health-check paths are excluded rather than one, since this host maps
    /// `/healthz/live` and `/healthz/ready` as well as `/healthz/version` - none of the three carries
    /// tenant data by inspection.</summary>
    private static (int RoutesAndHubMethods, int ClientSuppliedTenantIdRoutes) RoutesAndHubMethods(
        EndpointDataSource endpoints)
    {
        var routes = new HashSet<(string Method, string Path)>();
        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var rawPath = endpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(rawPath))
            {
                continue;
            }

            var path = rawPath.StartsWith('/') ? rawPath : "/" + rawPath;
            if (ExcludedPaths.Contains(path))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            foreach (var method in methods)
            {
                routes.Add((method, path));
            }
        }

        var clientSupplied = routes.Count(r => ClientSuppliedTenantIdSegment.IsMatch(r.Path));

        var hubMethods = CountHubMethods(typeof(CalendarOperatorHub));

        return (routes.Count + hubMethods, clientSupplied);
    }

    private static int CountHubMethods(Type hubType) =>
        hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(method => !method.IsSpecialName
                && method.Name is not (nameof(Hub.OnConnectedAsync) or nameof(Hub.OnDisconnectedAsync)));
}
