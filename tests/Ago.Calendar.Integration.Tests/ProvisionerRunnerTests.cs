using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Provisioner;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `ago-root#363`: <see cref="ProvisionerRunner"/> against a real Postgres - the one-shot admin path
/// chosen for AGO Calendar's first tenant, in front of exactly the mechanism `ChatOperatorBookingAuthorityTests`
/// already proved for <c>InviteOperatorHandler</c>'s rows (adr/0088). That file's own header explains
/// why the proof matters at this level rather than only at the Domain/Application one: what this item's
/// Done-when actually asks is "the owner signs in and reaches a screen with their own data", and the
/// closest honest proof of that without touching the live node is a real HTTP request against the real
/// console API, the real claims transformation and a real Postgres - so that is what these tests do.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvisionerRunnerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ConsoleApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ConsoleApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// The end-to-end proof: <see cref="ProvisionerRunner"/> writes a tenant knowing only the owner's
    /// email, and that owner's very first authenticated request - a new Keycloak subject the database
    /// has never seen, carrying only that email - reaches their own tenant's console data. Before this
    /// item, there was no way to reach this state at all (`ago-root#363`'s own gap: "authentication
    /// succeeds and lands on an account that does not exist").
    /// </summary>
    [Fact]
    public async Task ProvisionedTenant_TheOwnersFirstSignIn_ReachesTheirOwnTenantsConsoleData()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant(
            "Barbershop On Main", publicKey, "Dana Owner", ExternalSubjectId: null, [], "dana@example.com");

        await using var report = new StringWriter();
        var exitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, report, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Success, exitCode);
        Assert.Contains("Registered tenant", report.ToString(), StringComparison.Ordinal);

        // Written invited and unlinked - exactly the shape InviteOperatorHandler gives a colleague,
        // proven directly against the row before any HTTP request touches it.
        await using (var before = fixture.CreateDbContext())
        {
            var tenant = await before.Tenants.SingleAsync(t => t.PublicKey == new TenantPublicKey(publicKey));
            // .Include("_roles"): the same string-named navigation OperatorRepository's own queries
            // use throughout this product - Operator.Roles is a read-only projection over a private
            // backing field, so EF has no public property to include by lambda.
            var written = await before.Operators.Include("_roles").SingleAsync(o => o.TenantId == tenant.Id);
            Assert.True(written.IsAccountOwner);
            Assert.Null(written.ExternalSubjectId);
            Assert.Equal("dana@example.com", written.InvitedEmail!.Value.Value);
            Assert.Single(written.Roles);
        }

        // The owner's first sign-in: a subject the database has never seen, carrying only the email
        // the tool was given. No invite step ran for this row - it was created this way directly.
        var ownerSubject = $"kc-dana-{Guid.NewGuid():N}";
        using var firstSignIn = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/operators");
        firstSignIn.Headers.Add(ConsoleApiFactory.SubjectHeader, ownerSubject);
        firstSignIn.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "dana@example.com");
        var response = await _client.SendAsync(firstSignIn);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var operators = await response.Content.ReadFromJsonAsync<OperatorResponse[]>();
        var owner = Assert.Single(operators!);
        Assert.True(owner.IsAccountOwner);
        Assert.False(owner.IsInvited);
        Assert.Equal("dana@example.com", owner.InvitedEmail);
        Assert.Single(owner.RoleIds);

        // And the link is real, not merely displayed: the row's own ExternalSubjectId now holds this
        // request's subject.
        await using var after = fixture.CreateDbContext();
        var linked = await after.Operators.SingleAsync(o => o.Id == new OperatorId(owner.OperatorId));
        Assert.Equal(ownerSubject, linked.ExternalSubjectId);
    }

    /// <summary>Before the owner ever signs in, a stranger presenting an unrelated email is refused -
    /// the same "refuse rather than guess" property `ChatOperatorBookingAuthorityTests` proves for an
    /// invited colleague, now checked for an invited owner.</summary>
    [Fact]
    public async Task ProvisionedTenant_AStrangerWithAnUnrelatedEmail_IsRefused()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant(
            "Barbershop On Elm", publicKey, "Sam Owner", ExternalSubjectId: null, [], "sam@example.com");
        await using var report = new StringWriter();
        Assert.Equal(
            ProvisionerRunner.Success,
            await ProvisionerRunner.RunAsync(fixture.ConnectionString, command, report, CancellationToken.None));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/operators");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-stranger-{Guid.NewGuid():N}");
        request.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "nobody-provisioned-this@example.com");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A second run against the same public key writes nothing a second time - the tool
    /// reports the collision rather than crashing with a raw constraint-violation stack trace.</summary>
    [Fact]
    public async Task RunningItTwiceWithTheSamePublicKey_TheSecondRunWritesNothing()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant(
            "Barbershop On Oak", publicKey, "Robin Owner", ExternalSubjectId: null, [], "robin@example.com");

        await using var first = new StringWriter();
        Assert.Equal(
            ProvisionerRunner.Success,
            await ProvisionerRunner.RunAsync(fixture.ConnectionString, command, first, CancellationToken.None));

        await using var db = fixture.CreateDbContext();
        var countAfterFirst = await db.Tenants.CountAsync(t => t.PublicKey == new TenantPublicKey(publicKey));

        await using var second = new StringWriter();
        var secondExitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, second, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Failure, secondExitCode);
        Assert.Contains("ALREADY PROVISIONED", second.ToString(), StringComparison.Ordinal);

        await using var afterDb = fixture.CreateDbContext();
        var countAfterSecond = await afterDb.Tenants.CountAsync(t => t.PublicKey == new TenantPublicKey(publicKey));
        Assert.Equal(countAfterFirst, countAfterSecond);
        Assert.Equal(1, countAfterSecond);
    }

    /// <summary>A malformed owner email is refused before anything is written - the validation
    /// RegisterTenantHandler runs, surfaced through this tool's own report rather than an exception.</summary>
    [Fact]
    public async Task AMalformedOwnerEmail_IsRefusedAndWritesNothing()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant(
            "Barbershop On Pine", publicKey, "Alex Owner", ExternalSubjectId: null, [], "not-an-email");

        await using var report = new StringWriter();
        var exitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, report, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Failure, exitCode);
        Assert.Contains("REFUSED", report.ToString(), StringComparison.Ordinal);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Tenants.AnyAsync(t => t.PublicKey == new TenantPublicKey(publicKey)));
    }
}
