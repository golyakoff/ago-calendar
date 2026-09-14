using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `25-07` (`ago-root`): the ago-calendar half of a decision that item made explicitly rather than
/// leaving implicit - whether `Ago.Calendar.Api` (`Ago.Calendar.Module`, its own Minimal API routes,
/// the identical shape `Ago.Chat.Api` has) needs the same proof `ago-chat`'s own
/// <c>RouteHandlerDiRegistrationTests</c> was built to give: that every Minimal API route handler's
/// own parameters actually resolve from the real, composed <see cref="IServiceProvider"/>, the way
/// `ago-chat`'s `25-06` found out - live, on the demo stand - that nothing in its suite had ever
/// checked.
///
/// <para><b>Why this file is small where `ago-chat`'s own version needed a whole
/// <c>CompositionRoot</c> extraction.</b> `Ago.Calendar.Api`'s own `Program.cs` already ends in
/// <c>public partial class Program;</c> - added, per its own remarks, so
/// <c>Ago.Calendar.Integration.Tests</c> could point a real
/// <c>Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory&lt;Program&gt;</c>
/// (<see cref="CalendarApiFactory"/>) at the true entry point. `Ago.Chat.Api`'s own `Program.cs` had no
/// such seam before `25-07` - it was top-level statements with nothing a test could call into, which is
/// exactly why `OperatorInviteEndpointTests`/`ChannelStatusEndpointsTests` each hand-rebuilt their own
/// subset of its registrations instead. `Ago.Calendar.Api` never had that problem, so it never grew
/// that workaround.</para>
///
/// <para><b>What this means for the bug class itself, checked here rather than assumed.</b>
/// `WebApplicationFactory&lt;Program&gt;` builds and starts the real host (via `TestServer`) the first
/// time anything touches `.Server`/`.CreateClient()`/`.Services` - and starting the real host is what
/// runs `ApplicationBuilder.Build()`, which constructs every middleware once, including
/// `AuthorizationMiddleware`, whose own `AuthorizationPolicyCache` constructor is the exact trigger
/// `ago-chat`'s own `25-06` postmortem and its `25-07` test both name: it forces
/// `EndpointDataSource.Endpoints` to be built for every mapped route in the process, which is what
/// makes Minimal API's `RequestDelegateFactory` infer every mapped delegate's own parameter sources -
/// not lazily per request, at host start. Concretely: every existing file in this project that uses
/// <see cref="CalendarApiFactory"/> - <see cref="HealthzEndpointTests"/> included, which sends only an
/// anonymous `GET /healthz/live` - has therefore already been exercising this exact check as an
/// accidental side effect of existing at all, for as long as `Program.cs` has carried its own
/// `public partial class Program;` marker. That this suite passes today is real evidence
/// `Ago.Calendar.Api` carries no live `25-06`-class bug right now - but accidental coverage is exactly
/// the shape `25-07`'s own "false confidence is worse than no test" concern warns against: nothing
/// currently states this guarantee by name, so a future change to how these tests are fixture-shared
/// or mocked could quietly drop it and nothing would notice. This file makes it an explicit,
/// intentional assertion instead.</para>
///
/// <para><b>Why no structural, no-live-connection `ValidateOnBuild` check, unlike `ago-chat`'s own
/// sibling test.</b> That check exists there specifically to prove DI completeness *without* paying for
/// live Postgres/Redis/RabbitMQ containers - a real, separate value for a host whose own test
/// convention (`ChannelStatusEndpointsTests`/`OperatorInviteEndpointTests`) had never once built the
/// real composition root before `25-07`. `Ago.Calendar.Integration.Tests`' own convention is the
/// opposite already: every file in this project, `CalendarApiFactory` included, pays for real
/// Testcontainers throughout (`PostgresFixture`'s own Postgres/Redis/RabbitMQ). Adding a second,
/// connection-free path here would be a second composition, not a saving - this project's own
/// established norm already is the real one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RouteHandlerDiRegistrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>This suite's own real, observed count the day this test was written was 55 distinct
    /// mapped endpoints (`ago-root` `25-07`'s own worker report carries the exact figure) - 40 leaves
    /// a comfortable margin below it while still catching "a whole route group silently stopped being
    /// mapped", the identical reasoning `ago-chat`'s own sibling test gives for its own floor rather
    /// than a tight, release-to-release-tracked number.</summary>
    private const int MinimumExpectedRouteCount = 40;

    private CalendarApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new CalendarApiFactory(fixture);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void RealHostStarts_EveryMappedRouteHandlerParameterResolves()
    {
        // Building the TestServer is the whole reproduction - see this class's own remarks. No
        // request is sent, and none needs to be: accessing Server is what forces
        // WebApplicationFactory to build and start the real host, which is what builds
        // ApplicationBuilder's middleware pipeline once, which is what constructs
        // AuthorizationPolicyCache, which is what enumerates every mapped endpoint's own inferred
        // parameter sources.
        _ = _factory.Server;
    }

    [Fact]
    public void MappedRouteCountStaysAboveTheObservedFloor_SoASilentCoverageGapFailsLoudly()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        // Not "some routes exist" - "at least as many routes exist as this project already knows
        // Program.cs maps". Program.cs is the one and only route list (no CompositionRoot-style
        // extraction here - see this class's own remarks for why none was needed), so there is no
        // second, hand-maintained list for this count to be checked against; its purpose is catching
        // a route group that silently stopped being mapped at all, the same "assert your own coverage,
        // don't silently skip" discipline `25-07`'s own item text asks for (`tools/queue-audit.sh`'s
        // own precedent).
        Assert.True(
            endpoints.Count >= MinimumExpectedRouteCount,
            $"Expected at least {MinimumExpectedRouteCount} mapped routes; found {endpoints.Count}. "
                + "A drop this large means a route group silently stopped being mapped in Program.cs - "
                + "fix the gap, don't raise this floor to match.");
    }
}
