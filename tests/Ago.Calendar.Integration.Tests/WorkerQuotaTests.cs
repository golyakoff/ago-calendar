using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-07`/`adr/0093`'s own Done-when, against a real Postgres: granting N masters lets the calendar
/// accept N workers and refuses the (N+1)-th <b>inside its own transaction</b>, and lowering N
/// deactivates the excess rather than refusing the change or leaving the tenant over quota. Every
/// fixture seeds through <see cref="CalendarSeed.WriteAsync"/>, which already writes one active
/// worker directly (not through <see cref="CreateWorkerHandler"/>) - the quotas granted below always
/// account for that seeded worker rather than pretending the tenant starts empty.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerQuotaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ANewTenant_WithNoGrantAtAll_CannotCreateAWorker()
    {
        // `22-07`'s own default: zero until granted. No manual step should be needed to reach the
        // refusal - the tenant simply has not paid for the add-on yet, or the outbox has not
        // delivered the grant, and either way the calendar says no rather than "sure, unlimited".
        var seed = await CalendarSeed.WriteAsync(fixture);

        var result = await CreateWorkerAsync(seed, "Extra", "Person", CalendarSeed.Now.AddSeconds(1));

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", result.Error!.Value.Code);
    }

    [Fact]
    public async Task GrantingAQuota_AllowsExactlyThatManyActiveWorkers_AndRefusesTheNplusFirst_WithNothingWritten()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        // Room for the seeded worker plus one new one.
        await ApplyGrantAsync(seed.Tenant.Id, quota: 2, CalendarSeed.Now);

        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        Assert.True(second.IsSuccess);

        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));

        Assert.True(third.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", third.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(2, activeCount);
    }

    [Fact]
    public async Task LoweringTheQuota_DeactivatesTheMostRecentlyCreatedWorkersFirst_KeepingTheOldestActive()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 3, CalendarSeed.Now);

        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);

        // Down to one - only room for the tenant's oldest worker, the one CalendarSeed itself wrote.
        await ApplyGrantAsync(seed.Tenant.Id, quota: 1, CalendarSeed.Now.AddSeconds(3));

        await using var verify = fixture.CreateDbContext();
        var isActive = await verify.Workers
            .Where(w => w.TenantId == seed.Tenant.Id)
            .Select(w => new { w.Id, w.IsActive })
            .ToDictionaryAsync(w => w.Id, w => w.IsActive, CancellationToken.None);

        Assert.True(isActive[seed.Worker.Id]);
        Assert.False(isActive[second.Value]);
        Assert.False(isActive[third.Value]);

        // Nothing a shop typed was destroyed - deactivated, not deleted.
        Assert.Equal(3, isActive.Count);
    }

    [Fact]
    public async Task ReapplyingTheIdenticalGrant_IsANoOp_ProvingTheSnapshotIsIdempotent()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 1, CalendarSeed.Now);

        // The same fact, delivered a second time - the at-least-once redelivery
        // ModuleQuantityGrantedConsumer's own remarks describe.
        await ApplyGrantAsync(seed.Tenant.Id, quota: 1, CalendarSeed.Now.AddSeconds(1));

        await using var verify = fixture.CreateDbContext();
        var tenant = await verify.Tenants.SingleAsync(t => t.Id == seed.Tenant.Id, CancellationToken.None);
        Assert.Equal(1, tenant.WorkerQuota);

        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task RaisingTheQuotaBackUp_DoesNotReactivateAnyoneItPreviouslyDeactivated()
    {
        // `22-07`'s own report names this a deliberate, stated limitation rather than an oversight -
        // proven here so a future change that "fixes" it does so on purpose.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 2, CalendarSeed.Now);
        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        Assert.True(second.IsSuccess);

        await ApplyGrantAsync(seed.Tenant.Id, quota: 1, CalendarSeed.Now.AddSeconds(2));
        await ApplyGrantAsync(seed.Tenant.Id, quota: 5, CalendarSeed.Now.AddSeconds(3));

        await using var verify = fixture.CreateDbContext();
        var deactivated = await verify.Workers.SingleAsync(w => w.Id == second.Value, CancellationToken.None);
        Assert.False(deactivated.IsActive);
    }

    private async Task ApplyGrantAsync(TenantId tenantId, int quota, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        await new WorkerQuotaGrantStore(db).ApplyAsync(tenantId, quota, now, CancellationToken.None);
    }

    private async Task<Result<WorkerId>> CreateWorkerAsync(
        SeededTenant seed, string lastName, string firstName, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new CreateWorkerHandler(
            new BookingCalendarRepository(db), new ServiceRepository(db), new WorkerRepository(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)), new ClockIdGenerator(), new FixedClock(now));

        return await handler.HandleAsync(
            new CreateWorker(
                seed.OperatorId, seed.Tenant.Id, lastName, firstName, null, null, seed.Calendar.Id, []),
            CancellationToken.None);
    }

    /// <summary>Mints real UUIDv7 ids from the timestamp it is given, so workers created at
    /// deliberately different instants in these tests sort the way <see cref="WorkerQuotaPolicy"/>
    /// itself sorts them - the same time-ordered id property that method's own remarks rely on for
    /// its tie-break.</summary>
    private sealed class ClockIdGenerator : IIdGenerator
    {
        public Guid NewId(DateTimeOffset now) => Guid.CreateVersion7(now);
    }
}
