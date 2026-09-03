using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `ago-root#354`: <c>DevProvisioningEndpoints</c> and <c>PhoneVerificationDevEndpoints</c> each map
/// only outside Production (see each file's own remarks) - and until this test existed, that gate was
/// reasoned about rather than proved. A test that only checked the absent case would pass just as well
/// against a host that mapped nothing at all, so both directions are proved here, against the
/// identical build: the routes exist under a non-Production environment and are gone under
/// Production.
///
/// <para><b>What is asserted is route presence, never the configuration value that drives it.</b> A
/// request that never reaches <c>RegisterTenantHandler</c> or writes a tenant row still answers the
/// routing question, because a route that exists fails model binding with 400 while a route that does
/// not exist answers 404 regardless of what the request body contains - the same distinction
/// <c>PhoneVerificationDevEndpoints</c>' own required <c>phone</c> query parameter gives for free: 400
/// once mapped, 404 when it is not, without ever calling <c>FakePhoneVerificationSender</c>.</para>
///
/// <para>The environment is forced explicitly with <see cref="IWebHostBuilder.UseEnvironment"/> in
/// both directions, rather than relying on the test host's own default for one of them - the point of
/// this test is that the gate holds under a stated environment, which is exactly the property this
/// item's manifest change gives the real hosts.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DevEndpointsEnvironmentGateTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DevEndpointsFactory? _factory;
    private HttpClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.BadRequest)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task TheDevTenantRoute_TracksTheEnvironment(string environment, HttpStatusCode expected)
    {
        UseHost(environment);

        // Malformed JSON, not an empty body: what proves the route exists is ASP.NET Core's own
        // body-binding failure (400) ahead of RegisterTenantHandler ever running, so this never
        // creates a tenant regardless of which branch is under test.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dev/tenants")
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        };

        var response = await _client!.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.BadRequest)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task TheLastCodeRoute_TracksTheEnvironment(string environment, HttpStatusCode expected)
    {
        UseHost(environment);

        // No `phone` query string: a mapped route answers 400 (required parameter missing) before
        // FakePhoneVerificationSender is ever asked anything; an unmapped route answers 404 regardless.
        var response = await _client!.GetAsync("/dev/phone-verifications/last-code");

        Assert.Equal(expected, response.StatusCode);
    }

    private void UseHost(string environment)
    {
        _factory = new DevEndpointsFactory(fixture, environment);
        _client = _factory.CreateClient();
    }
}

/// <summary><see cref="CalendarApiFactory"/> with the environment forced explicitly, so this test
/// depends on neither the test host's own default nor on any ambient <c>ASPNETCORE_ENVIRONMENT</c> in
/// the process running the suite.</summary>
internal sealed class DevEndpointsFactory(PostgresFixture fixture, string environment)
    : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseEnvironment(environment);
    }
}
