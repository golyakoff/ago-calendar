using Ago.Calendar.Api.Cors;
using Ago.Calendar.Application.UseCases.Cors;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// <b>Layer 1</b> of `5-01`'s two-layer CORS model, against a real Postgres: does an origin get an
/// <c>Access-Control-Allow-Origin</c> at all.
///
/// <para><b>Exercising <see cref="ICorsPolicyProvider.GetPolicyAsync"/> directly, not through a full
/// HTTP pipeline</b> - the same call `5-01`'s own <c>SiteOriginCorsPolicyProviderTests</c> made, and
/// for the same reason: the ASP.NET Core CORS *middleware* that turns a non-null <c>CorsPolicy</c>
/// into real response headers is framework code this project does not re-test. What is this
/// product's own code is the decision, and the decision is what a returned policy is.</para>
///
/// <para><b>The interesting assertion is the one about a policy that is granted.</b> A granted policy
/// is not a tenant boundary - it says some tenant approved the origin, nothing more - and
/// <see cref="OriginAuthorizationTests"/> is where that gap is shown to be closed by layer 2.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantOriginCorsPolicyProviderTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AnOriginSomeTenantAllows_GetsAPolicyEchoingThatExactOrigin()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, allowedOrigins: ["https://shop-a.example"]);
        Assert.NotNull(seed);

        var policy = await ResolveAsync("https://shop-a.example");

        Assert.NotNull(policy);

        // The exact origin, never a wildcard - api-design.md's own words. A '*' here would make every
        // page on the internet a legitimate embedder of every tenant.
        Assert.Equal(["https://shop-a.example"], policy.Origins);
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public async Task AnOriginNoTenantAllows_GetsNoPolicyAtAll()
    {
        await CalendarSeed.WriteAsync(fixture, allowedOrigins: ["https://shop-b.example"]);

        // Null, not an empty policy: a null policy makes the middleware write no
        // Access-Control-Allow-Origin header at all, which is what a browser needs to see in order to
        // refuse the response.
        Assert.Null(await ResolveAsync("https://nobody.example"));
    }

    [Fact]
    public async Task ARequestWithNoOriginHeader_GetsNoPolicy()
    {
        // Not a browser cross-origin request at all. Nothing to allow, and nothing to deny.
        Assert.Null(await ResolveAsync(origin: null));
    }

    [Fact]
    public async Task AnOriginAllowedByOneTenant_IsAllowedWhileAnotherTenantExistsWithADifferentList()
    {
        // The precise shape of layer 1's weakness, made explicit here so that its counterpart in
        // OriginAuthorizationTests reads as the fix rather than as a duplicate.
        var a = await CalendarSeed.WriteAsync(fixture, allowedOrigins: ["https://tenant-a.example"]);
        var b = await CalendarSeed.WriteAsync(fixture, allowedOrigins: ["https://tenant-b.example"]);
        Assert.NotEqual(a.Tenant.Id, b.Tenant.Id);

        Assert.NotNull(await ResolveAsync("https://tenant-a.example"));
        Assert.NotNull(await ResolveAsync("https://tenant-b.example"));
    }

    [Fact]
    public async Task TheConsolesOwnOrigin_IsAllowedFromConfigurationAndCarriesAuthorization()
    {
        // Configuration, not tenant data - see TenantOriginCorsPolicyProvider for why the two lists
        // never merge. No tenant lists this origin, and it is still allowed.
        var policy = await ResolveAsync("https://console.example", consoleOrigins: ["https://console.example/"]);

        Assert.NotNull(policy);
        Assert.Contains("Authorization", policy.Headers);

        // `22-14`/`adr/0100`: a custom request header a preflight does not name is a header the
        // browser refuses to send, so this line is the difference between the tenant switcher working
        // and every calendar call failing with a CORS error that names nothing.
        Assert.Contains(
            Ago.Calendar.Api.Auth.OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, policy.Headers);
    }

    [Fact]
    public async Task ATenantCannotGrantItselfTheConsolePolicy()
    {
        // A tenant listing the console's origin gets the *public* policy for it, which carries no
        // Authorization header - so the two lists cannot be crossed by editing tenant data.
        await CalendarSeed.WriteAsync(fixture, allowedOrigins: ["https://console.example"]);

        var policy = await ResolveAsync("https://console.example");

        Assert.NotNull(policy);
        Assert.DoesNotContain("Authorization", policy.Headers);

        // And it cannot name a tenant either: the public policy carries neither header, so a tenant
        // that listed the console's origin gains no way to send `22-14`'s active-tenant signal.
        Assert.DoesNotContain(
            Ago.Calendar.Api.Auth.OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, policy.Headers);
    }

    /// <summary>
    /// Builds the provider over the real repository on the real container, with a service scope
    /// exactly as the running host gives it one.
    /// </summary>
    private async Task<CorsPolicy?> ResolveAsync(string? origin, params string[] consoleOrigins)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddScoped<ITenantRepository>(
            provider => new TenantRepository(provider.GetRequiredService<AgoCalendarDbContext>()));
        services.AddScoped<CheckTenantOriginHandler>();

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        var policyProvider = new TenantOriginCorsPolicyProvider(
            provider.GetRequiredService<IServiceScopeFactory>(), new ConsoleOrigins(consoleOrigins));

        return await policyProvider.GetPolicyAsync(context, policyName: null);
    }
}
