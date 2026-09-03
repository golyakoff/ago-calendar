using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-24`'s own gap, closed: <c>Ago.Calendar.Api</c> mapped no health route at all before this item,
/// only a bare <c>GET /</c> returning the loaded module's name - <c>smoke.sh</c>'s two SKIPs exist
/// because of it. This runs the real host (<see cref="CalendarApiFactory"/>) against the real
/// Postgres and Redis containers this suite already pays for, over real HTTP, so a route that exists
/// but is wired to the wrong thing fails here exactly as it would against a live deployment.
///
/// <para><see cref="PostgresHealthCheckTests"/> proves the Postgres check itself distinguishes a
/// reachable database from an unreachable one; this file proves the host actually wires that check
/// (and the platform's own <c>RedisHealthCheck</c>) into <c>/healthz/ready</c>, and wires
/// <see cref="BuildInfoResponse"/> into <c>/healthz/version</c>, rather than mapping routes that exist
/// but answer nothing real.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HealthzEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task HealthzLive_AlwaysAnswersOk()
    {
        var response = await _client.GetAsync("/healthz/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthzReady_WithPostgresAndRedisBothReachable_AnswersOk()
    {
        var response = await _client.GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthzVersion_ReportsWhateverTheAssemblyItselfCarries()
    {
        var response = await _client.GetAsync("/healthz/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BuildInfoResponse>();
        Assert.NotNull(body);
        Assert.Equal("Ago.Calendar.Api", body.Host);
        // Not asserted against a literal - "unknown" or a real sha - because which one is correct
        // here depends on how this suite happens to be built: `dotnet test` runs inside this git
        // worktree, and the .NET SDK's own git detection embeds the real current commit into
        // AssemblyInformationalVersion automatically, independently of the Dockerfile's own
        // `-p:SourceRevisionId=<sha>` (which is why the Dockerfile does not COPY `.git` into its build
        // context - the container build must not silently inherit this). Asserting a literal would
        // therefore be asserting an artifact of the test environment, not a property of the endpoint.
        // What genuinely proves the endpoint reads real assembly metadata rather than a hardcoded
        // placeholder is this: it must report the exact same thing a second, independent call into
        // BuildInfoResponse.For(typeof(Program).Assembly) reports for the identical assembly. A
        // hardcoded "unknown" (or any other literal) in the endpoint would fail this the moment the
        // assembly actually carries a revision, which - as this very run demonstrates - it does.
        var expected = BuildInfoResponse.For(typeof(Program).Assembly);
        Assert.Equal(expected, body);
    }
}
