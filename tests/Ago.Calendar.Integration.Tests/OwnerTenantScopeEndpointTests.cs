using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ago.Calendar.Api.Auth;
using Ago.Calendar.Api.Owner;
using Ago.Calendar.Contracts;
using Ago.Calendar.Infrastructure.TenantScopeDiagnostics;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `24-17`'s own Done-when for this product: `GET /api/v1/owner/tenant-isolation` answers the platform
/// owner and refuses everyone else.
///
/// <para><b>No Postgres, no Keycloak - unlike `CalendarApiFactory`'s own suites.</b>
/// `RequirePlatformOwner` reads only the validated token's `realm_access.roles` claim
/// (`PlatformOwnerAuthorizationHandler`'s own remarks: copied from `ago-chat`'s identical boundary),
/// never this product's own `role_assignment_projections` table - so this test host needs neither
/// container, the same reasoning `ago-chat`'s own `OwnerTenantIsolationEndpointTests` already gives for
/// its own stripped host. The fake authentication scheme below is narrower even than
/// `ConsoleApiFactory`'s own `HeaderSubjectAuthenticationHandler`: it never touches `OperatorIdentityClaimsTransformation`
/// at all, because this route's whole access-control story does not run through it.</para>
/// </summary>
public sealed class OwnerTenantScopeEndpointTests
{
    private const string Route = "/api/v1/owner/tenant-isolation";

    [Fact]
    public async Task OwnerToken_GetsTheLiveSnapshot()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, TestAuthenticationHandler.Owner);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantScopeSummaryResponse>();
        Assert.NotNull(body);
        // Real figures from the real Ago.Calendar.Application.dll sitting next to this test host -
        // the same numbers Ago.Calendar.Architecture.Tests.CalendarTenantScopeInspectorTests proves
        // agree with a direct CalendarTenantScopeRule scan.
        Assert.True(body.EntryPoints > 0);
        Assert.True(body.HandlerClasses > 0);
        Assert.True(body.RbacGated > 0);
        Assert.Equal(body.EntryPoints - body.RbacGated, body.NotGated);
        Assert.Equal(body.NotGated, body.NotGatedKeys.Count);
        Assert.True(body.RoutesAndHubMethods >= 0);
        Assert.True(body.ClientSuppliedTenantIdRoutes >= 0);
    }

    [Fact]
    public async Task AuthenticatedNonOwnerToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, TestAuthenticationHandler.NonOwner);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, subjectKind: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }

    private static HttpClient CreateClient(WebApplication host, string? subjectKind)
    {
        var client = host.GetTestClient();
        if (subjectKind is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, subjectKind);
        }

        return client;
    }

    private static async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        // The production registration, Ago.Calendar.Api's own Program.cs AddTenantScopeDiagnostics
        // call - the real CalendarTenantScopeInspector, pointed at this test host's own
        // Ago.Calendar.Application.dll.
        builder.Services.AddTenantScopeDiagnostics();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            // AuthenticationSetup.cs's own declaration, reproduced verbatim.
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapOwnerTenantScopeEndpoint();

        await app.StartAsync();
        return app;
    }

    /// <summary>A fake authentication scheme narrower than `CalendarApiFactory`'s own
    /// `HeaderSubjectAuthenticationHandler`: it mints either a bare authenticated principal (no
    /// `realm_access` claim at all - the shape any ordinary Keycloak-issued operator token has) or one
    /// that also carries the exact `realm_access` JSON `PlatformOwnerRealmRole.IsHeldBy` reads - never
    /// an `operator_id`/`tenant_id` claim, because `RequirePlatformOwner` never asks for either.</summary>
    private sealed class TestAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestSubject";
        public const string SubjectHeader = "X-Test-Auth";
        public const string Owner = "owner";
        public const string NonOwner = "non-owner";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var kind = Request.Headers[SubjectHeader].ToString();
            if (string.IsNullOrWhiteSpace(kind))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", "test-subject") };
            if (kind == Owner)
            {
                claims.Add(new Claim(
                    PlatformOwnerRealmRole.RealmAccessClaimType,
                    /*lang=json,strict*/ "{\"roles\":[\"" + PlatformOwnerRequirement.RealmRoleName + "\"]}"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
