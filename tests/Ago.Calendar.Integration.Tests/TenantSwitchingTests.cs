using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Api.Auth;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-14`/`adr/0100`, demonstrated over real HTTP against a real Postgres rather than asserted at
/// the resolver: a person with calendar grants on two accounts reaches the calendar in each and sees
/// only that one's data, a caller naming a tenant they hold nothing in is refused, and a person with
/// one tenancy is unaffected either way.
///
/// <para>The header, not a fake principal, is what these send - <see cref="ConsoleApiFactory"/>
/// replaces exactly one thing (proof that Keycloak signed a token for a subject) and leaves
/// <c>OperatorIdentityClaimsTransformation</c>, the <c>calendar-operator</c> policy and the real
/// <c>PermissionChecker</c> all running, which is the only arrangement in which "which line refuses
/// this" is a question the test can actually answer.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantSwitchingTests(PostgresFixture fixture)
{
    /// <summary>
    /// The item's own promise, and the thing that fails on `main`: two grants, two calendars, and the
    /// person can work in both. Before this item the projection named two tenants, the resolver
    /// refused to guess, no <c>tenant_id</c> claim was added and every one of these calls was a
    /// <c>403</c> - indistinguishable from never having been granted anything (`19-03`'s "absent"
    /// console).
    ///
    /// <para>Asserted on the <i>workers</i> list rather than on a status code alone, because "reached
    /// the calendar" and "reached the right calendar" are different claims and only the second one is
    /// worth having: each request returns exactly its own tenant's worker and never the other's.</para>
    /// </summary>
    [Fact]
    public async Task APersonWithGrantsInTwoTenants_ReachesEachOne_AndSeesOnlyThatTenantsData()
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var first = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);
        var second = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        var firstWorkers = await ListWorkerIdsAsync(client, subject, first.Tenant.Id.Value);
        var secondWorkers = await ListWorkerIdsAsync(client, subject, second.Tenant.Id.Value);

        Assert.Equal([first.Worker.Id.Value], firstWorkers);
        Assert.Equal([second.Worker.Id.Value], secondWorkers);
    }

    /// <summary>
    /// The refusal, over real HTTP. A caller naming a tenant their projection carries no row for gets
    /// a <c>403</c> from <c>CalendarClaims.OperatorPolicy</c>, because
    /// <c>RoleAssignmentProjectionStore.ResolveTenantAsync</c>'s requested-tenant branch returned
    /// <see langword="null"/> and no <c>tenant_id</c> claim was ever minted.
    ///
    /// <para><b>Not a fallback to the tenancy they do hold</b> - which is the property this test is
    /// really for. The caller here has exactly one real tenancy, so a resolver that ignored an
    /// unrecognised header (`ago-chat`'s own choice, for a reason that does not apply here) would
    /// have answered <c>204</c> by falling back to it, and the request would have acted in a tenant
    /// it did not ask for.</para>
    /// </summary>
    [Fact]
    public async Task ACallerNamingATenantTheyHoldNoGrantIn_IsRefused_AndIsNotFallenBackToTheirOwn()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var somebodyElses = await CalendarSeed.WriteAsync(fixture);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = Configure(mine.ExternalSubjectId, somebodyElses.Tenant.Id.Value);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Refused by the policy, before any handler ran - and the body is how that is checkable.
        // `CalendarClaims.OperatorPolicy` answers a bare 403 with no body; a handler's own refusal
        // would be `ErrorExtensions.ToProblem`'s problem+json naming `configuration.forbidden`. Both
        // are 403, so a status-only assertion would pass just as happily if
        // `ResolveTenantAsync` trusted the header outright and `IPermissionChecker` caught it a
        // moment later. That double defence is real and worth having; it is not what this test is
        // for, and without this line the test does not fail when the resolver stops checking.
        Assert.Empty(await response.Content.ReadAsStringAsync());

        // And the tenant they named was not touched: its origins are still the empty set the seed
        // wrote, not the value this request tried to set. A 403 that had still written something
        // would be the worst possible pass.
        await using var db = fixture.CreateDbContext();
        var victim = await db.Tenants.SingleAsync(t => t.Id == somebodyElses.Tenant.Id);
        Assert.Empty(victim.AllowedOrigins);
    }

    /// <summary>
    /// The resolver itself, against a real Postgres - the line the HTTP test above proves is reached,
    /// read directly so a failure names it rather than a status code.
    ///
    /// <para>Both directions in one test on purpose: "returns the tenant it was asked for" and
    /// "returns null for one it was not granted" are the same promise seen from two sides, and a
    /// mutation that broke either would be caught by whichever half it broke.</para>
    /// </summary>
    [Fact]
    public async Task ResolveTenant_AnswersOnlyOutOfThisOperatorsOwnProjectionRows()
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var mine = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);
        var alsoMine = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);
        var somebodyElses = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var store = new RoleAssignmentProjectionStore(db);
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        Assert.Equal(mine.Tenant.Id, await store.ResolveTenantAsync(operatorId, mine.Tenant.Id, default));
        Assert.Equal(alsoMine.Tenant.Id, await store.ResolveTenantAsync(operatorId, alsoMine.Tenant.Id, default));

        // Named, not held: null, and specifically not either of the two this operator does hold.
        Assert.Null(await store.ResolveTenantAsync(operatorId, somebodyElses.Tenant.Id, default));

        // Nothing named, two rows: still unresolved, exactly as before `22-14`.
        Assert.Null(await store.ResolveTenantAsync(operatorId, requestedTenantId: null, default));

        // Nothing named, one row: that row, exactly as before `22-14`. The one-tenant path, pinned
        // where it is decided rather than only over HTTP.
        var onlyOne = OperatorId.FromExternalSubjectId(somebodyElses.ExternalSubjectId);
        Assert.Equal(somebodyElses.Tenant.Id, await store.ResolveTenantAsync(onlyOne, requestedTenantId: null, default));
    }

    /// <summary>
    /// A tenancy the caller does hold, named explicitly, works - the same call as the refusal above,
    /// differing only in which tenant is named. Without this the refusal proves nothing but that the
    /// header can break a request.
    /// </summary>
    [Fact]
    public async Task ACallerNamingATenantTheyDoHold_IsAllowed()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = Configure(mine.ExternalSubjectId, mine.Tenant.Id.Value);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// The regression case, stated as the item's own constraint: a one-tenancy operator must not
    /// notice this item at all. Both shapes are exercised because the console sends the header
    /// unconditionally (`13-07`'s <c>resolveActiveSite</c> returns the single tenancy rather than
    /// <see langword="null"/>) while a direct API caller - or a console build predating this - sends
    /// none, and both must resolve identically.
    /// </summary>
    [Fact]
    public async Task AnOperatorWithOneTenancy_IsUnaffected_WithOrWithoutTheHeader()
    {
        var only = await CalendarSeed.WriteAsync(fixture);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var withoutHeader = Configure(only.ExternalSubjectId, tenantId: null);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(withoutHeader)).StatusCode);

        using var withHeader = Configure(only.ExternalSubjectId, only.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(withHeader)).StatusCode);
    }

    /// <summary>
    /// A header that names nothing at all - not a <see cref="Guid"/> - reads as "did not ask", so the
    /// ordinary single-tenancy resolution still applies. Deliberately different from a well-formed id
    /// this operator holds nothing in (refused, above): one has selected a tenant and been caught, the
    /// other has selected none. Pinned by a test because it is exactly the kind of distinction a
    /// later reader would otherwise flatten.
    /// </summary>
    [Fact]
    public async Task AMalformedHeader_ReadsAsNoTenantRequested_RatherThanAsARefusal()
    {
        var only = await CalendarSeed.WriteAsync(fixture);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/console/configuration/allowed-origins");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, only.ExternalSubjectId);
        request.Headers.Add(OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, "not-a-guid");
        request.Content = JsonContent.Create(new SetAllowedOriginsRequest(["https://shop.example.com"]));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// The enumeration the console cannot offer a choice without: both tenancies, named, for an
    /// identity that has no resolvable <c>tenant_id</c> claim of its own - which is why this route
    /// carries <c>CalendarClaims.IdentityPolicy</c> and not the operator policy. The strictly gated
    /// route is called first, and refused, so the test proves the two policies really do differ here
    /// rather than assuming it.
    /// </summary>
    [Fact]
    public async Task TheTenanciesRead_AnswersAnIdentityThatNoOperatorRouteWillServeYet()
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var first = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);
        var second = await CalendarSeed.WriteAsync(fixture, externalSubjectId: subject);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using (var operatorRoute = Configure(subject, tenantId: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(operatorRoute)).StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/tenancies");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenanciesResponse>();
        Assert.Equal(
            new[] { first.Tenant.Id.Value, second.Tenant.Id.Value }.OrderBy(id => id).ToArray(),
            body!.Tenancies.Select(t => t.TenantId).OrderBy(id => id).ToArray());
        Assert.All(body.Tenancies, tenancy => Assert.Equal("Barbershop", tenancy.TenantName));
    }

    /// <summary>An identity this product has never heard of gets an empty list and a <c>200</c>, not a
    /// refusal - the state the console has to be able to tell apart from "you have a calendar, just
    /// not in the shop you are looking at".</summary>
    [Fact]
    public async Task TheTenanciesRead_AnswersAnIdentityWithNoGrantsAtAll_WithAnEmptyList()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/tenancies");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-{CalendarSeed.NewId():N}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<TenanciesResponse>())!.Tenancies);
    }

    /// <summary>A write that is harmless, tenant-scoped and already used by
    /// <see cref="RoleAssignmentProjectionDemonstrationTests"/> to mean "this principal really did
    /// resolve to a tenant" - reused here rather than a second route invented, so a failure is about
    /// resolution and never about the endpoint.</summary>
    private static HttpRequestMessage Configure(string subject, Guid? tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/console/configuration/allowed-origins");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        if (tenantId is { } tenant)
        {
            request.Headers.Add(OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, tenant.ToString());
        }

        request.Content = JsonContent.Create(new SetAllowedOriginsRequest(["https://shop.example.com"]));
        return request;
    }

    private static async Task<Guid[]> ListWorkerIdsAsync(HttpClient client, string subject, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/workers");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        request.Headers.Add(OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, tenantId.ToString());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var workers = await response.Content.ReadFromJsonAsync<WorkerResponse[]>();
        return [.. workers!.Select(worker => worker.WorkerId)];
    }
}
