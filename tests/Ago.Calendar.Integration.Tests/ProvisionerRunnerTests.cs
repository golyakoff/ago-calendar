using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Domain;
using Ago.Calendar.Provisioner;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `ago-root#363`: <see cref="ProvisionerRunner"/> against a real Postgres - the one-shot admin path
/// for AGO Calendar's first tenant.
///
/// <para><b>`22-05`/`adr/0093`: no owner-identity coverage left to carry here.</b> This file used to
/// prove the whole invite-a-colleague-shaped account-owner flow (adr/0088's mechanism, reused a
/// caller earlier) end to end over real HTTP - a Keycloak subject nobody had seen before, resolving
/// to its own tenant through the email fallback. That mechanism is gone (see
/// <c>OperatorIdentityClaimsTransformation</c>'s own remarks for what replaced it: nothing
/// calendar-specific, because there is only one invite now, on the account side). What remains here
/// is `22-03`'s own tenant-id-provenance coverage, unchanged in substance, plus the collision
/// refusals every provisioning tool needs regardless of what identity model sits above it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvisionerRunnerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Provisioned_WritesExactlyOneTenant_ReadableBackFromAFreshContext()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant("Barbershop On Main", publicKey, []);

        await using var report = new StringWriter();
        var exitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, report, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Success, exitCode);
        Assert.Contains("Registered tenant", report.ToString(), StringComparison.Ordinal);

        // A fresh context, not the one the run itself used - so this reads back what Postgres
        // actually holds rather than an EF change-tracker entity the run happened to still have in
        // memory.
        await using var db = fixture.CreateDbContext();
        var tenant = await db.Tenants.SingleAsync(t => t.PublicKey == new TenantPublicKey(publicKey));
        Assert.Equal("Barbershop On Main", tenant.Name);
    }

    /// <summary>A second run against the same public key writes nothing a second time - the tool
    /// reports the collision rather than crashing with a raw constraint-violation stack trace.</summary>
    [Fact]
    public async Task RunningItTwiceWithTheSamePublicKey_TheSecondRunWritesNothing()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var command = new RegisterTenant("Barbershop On Oak", publicKey, []);

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

    /// <summary>A malformed public key is refused before anything is written - the validation
    /// RegisterTenantHandler runs, surfaced through this tool's own report rather than an
    /// exception.</summary>
    [Fact]
    public async Task AMalformedPublicKey_IsRefusedAndWritesNothing()
    {
        var command = new RegisterTenant("Barbershop On Pine", string.Empty, []);

        await using var report = new StringWriter();
        var exitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, report, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Failure, exitCode);
        Assert.Contains("REFUSED", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// `22-03`/adr/0093, the end-to-end proof this item asks for: a tenant provisioned with a
    /// caller-supplied id - standing in for the account id AGO Chat calls <c>SiteId</c> - is read back
    /// from a fresh <see cref="Ago.Calendar.Infrastructure.Postgres.Persistence.AgoCalendarDbContext"/>
    /// as that exact value, not merely returned by the handler in-process.
    /// </summary>
    [Fact]
    public async Task ProvisionedWithASuppliedTenantId_TheTenantRowReadsBackAsThatExactId()
    {
        var publicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var accountId = new TenantId(Guid.NewGuid());
        var command = new RegisterTenant("Barbershop On Birch", publicKey, [], accountId);

        await using var report = new StringWriter();
        var exitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, command, report, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Success, exitCode);
        Assert.Contains(accountId.Value.ToString(), report.ToString(), StringComparison.Ordinal);

        await using var db = fixture.CreateDbContext();
        var tenant = await db.Tenants.SingleAsync(t => t.PublicKey == new TenantPublicKey(publicKey));
        Assert.Equal(accountId, tenant.Id);
    }

    /// <summary>
    /// The other half of "accepting an id is not the same as trusting one": nothing in the calendar
    /// can confirm a supplied id names a real account, so the only defence available today is the
    /// store's own primary key - a second run repeating the same id is refused exactly as a second run
    /// repeating the same public key already was, and writes nothing.
    /// </summary>
    [Fact]
    public async Task RunningItTwiceWithTheSameSuppliedTenantId_TheSecondRunWritesNothing()
    {
        var accountId = new TenantId(Guid.NewGuid());
        var firstPublicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var firstCommand = new RegisterTenant("Barbershop On Cedar", firstPublicKey, [], accountId);

        await using var first = new StringWriter();
        Assert.Equal(
            ProvisionerRunner.Success,
            await ProvisionerRunner.RunAsync(fixture.ConnectionString, firstCommand, first, CancellationToken.None));

        // A different public key, the same account id - the id is what collides.
        var secondPublicKey = $"shop-{Guid.NewGuid():N}"[..24];
        var secondCommand = new RegisterTenant("Barbershop On Cedar Annex", secondPublicKey, [], accountId);

        await using var second = new StringWriter();
        var secondExitCode = await ProvisionerRunner.RunAsync(
            fixture.ConnectionString, secondCommand, second, CancellationToken.None);

        Assert.Equal(ProvisionerRunner.Failure, secondExitCode);
        Assert.Contains("ALREADY PROVISIONED", second.ToString(), StringComparison.Ordinal);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Tenants.CountAsync(t => t.Id == accountId));
        Assert.False(await db.Tenants.AnyAsync(t => t.PublicKey == new TenantPublicKey(secondPublicKey)));
    }
}
