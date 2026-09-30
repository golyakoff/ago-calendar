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
///
/// <para>`26-317`: every comparison here is really against <see cref="Tenant.EffectiveWorkerQuota"/>,
/// not the raw grant - a grant of 0 or 1 is floored to <see cref="Tenant.DefaultWorkerQuota"/> before
/// it gates anything, so several tests below seed a wider roster than the number they grant purely to
/// stay clear of the floor and exercise the ordinary downgrade rule on its own terms. The floor itself
/// has its own tests just below.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerQuotaTests(PostgresFixture fixture)
{
    /// <summary>
    /// `26-317`: the computed floor's own Done-when - a fresh tenant (raw `worker_quota` 0, `ago-chat`'s
    /// own grant never having landed) can still create up to <see cref="Tenant.DefaultWorkerQuota"/>
    /// masters with no manual owner grant at all, and only the one past that floor is refused. Fails
    /// on the pre-`26-317` code, where a `worker_quota=0` tenant could create zero.
    /// </summary>
    [Fact]
    public async Task ANewTenant_WithNoGrantAtAll_CanCreateUpToTheDefaultFloor_AndTheOneAfterIsRefused()
    {
        // CalendarSeed already writes one active worker directly, so exactly one more fits under the
        // default floor of 2 before the third is refused.
        var seed = await CalendarSeed.WriteAsync(fixture);

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

    /// <summary>`26-317`: a tenant granted a number above the floor is bound by the grant, not the
    /// floor - the floor only ever raises a shortfall, it never lowers a real grant.</summary>
    [Fact]
    public async Task ATenantGrantedThree_CanCreateThreeActiveWorkers_AndTheFourthIsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 3, CalendarSeed.Now);

        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);

        var fourth = await CreateWorkerAsync(seed, "Four", "D", CalendarSeed.Now.AddSeconds(3));

        Assert.True(fourth.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", fourth.Error!.Value.Code);
    }

    /// <summary>`26-317`: the floor's own grandfather clause - a tenant already seeded above the floor
    /// (by direct row insertion here, standing in for a tenant that already existed before this item
    /// shipped) is never deactivated or otherwise touched by the floor's introduction, because the
    /// floor only ever raises <see cref="Tenant.WorkerQuota"/>, never lowers it.</summary>
    [Fact]
    public async Task ATenantSeededAtFour_CreatesAllFourActiveWorkers_WithNoDeactivation()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, workerQuota: 4);

        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        var fourth = await CreateWorkerAsync(seed, "Four", "D", CalendarSeed.Now.AddSeconds(3));

        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);
        Assert.True(fourth.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(4, activeCount);
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

        // Down to one - but `26-317`'s own default floor of two means the grant is compared against
        // GREATEST(1, 2), so only the one worker past that floor is deactivated, not two: the most
        // recently created, "Three".
        await ApplyGrantAsync(seed.Tenant.Id, quota: 1, CalendarSeed.Now.AddSeconds(3));

        await using var verify = fixture.CreateDbContext();
        var isActive = await verify.Workers
            .Where(w => w.TenantId == seed.Tenant.Id)
            .Select(w => new { w.Id, w.IsActive })
            .ToDictionaryAsync(w => w.Id, w => w.IsActive, CancellationToken.None);

        Assert.True(isActive[seed.Worker.Id]);
        Assert.True(isActive[second.Value]);
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
        // proven here so a future change that "fixes" it does so on purpose. `26-317`: the roster is
        // shifted two workers wider than before so the lowered grant below still deactivates someone
        // past the default floor of two, rather than being absorbed by it with nothing to prove.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 4, CalendarSeed.Now);
        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);

        // Down to the default floor of two - deactivates the most recently created worker, "Three".
        await ApplyGrantAsync(seed.Tenant.Id, quota: 2, CalendarSeed.Now.AddSeconds(3));
        await ApplyGrantAsync(seed.Tenant.Id, quota: 5, CalendarSeed.Now.AddSeconds(4));

        await using var verify = fixture.CreateDbContext();
        var deactivated = await verify.Workers.SingleAsync(w => w.Id == third.Value, CancellationToken.None);
        Assert.False(deactivated.IsActive);
    }

    [Fact]
    public async Task ReactivatingADeactivatedWorker_PastTheQuota_IsRefused_WithNothingWritten()
    {
        // `22-23`: the bypass `22-07`'s own downgrade rule leaves open - the excess is deactivated,
        // not deleted, and the ordinary edit endpoint used to let anyone flip it straight back on.
        // `26-317`: the roster is shifted two workers wider than before for the same reason as
        // RaisingTheQuotaBackUp_DoesNotReactivateAnyoneItPreviouslyDeactivated above.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 4, CalendarSeed.Now);
        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);

        // Down to the default floor of two - 22-07's own rule deactivates the most recently created
        // worker, "Three".
        await ApplyGrantAsync(seed.Tenant.Id, quota: 2, CalendarSeed.Now.AddSeconds(3));

        // The request also renames the worker, so "nothing written" has to cover the whole call, not
        // only the activity flag.
        var result = await UpdateWorkerAsync(
            seed, third.Value, lastName: "Renamed", firstName: "C", isActive: true,
            now: CalendarSeed.Now.AddSeconds(4));

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", result.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        var worker = await verify.Workers.SingleAsync(w => w.Id == third.Value, CancellationToken.None);
        Assert.False(worker.IsActive);
        Assert.Equal("Three", worker.LastName);
    }

    [Fact]
    public async Task ReactivatingADeactivatedWorker_WithinTheQuota_Succeeds()
    {
        // `26-317`: the roster is shifted two workers wider than before for the same reason as
        // RaisingTheQuotaBackUp_DoesNotReactivateAnyoneItPreviouslyDeactivated above.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 4, CalendarSeed.Now);
        var second = await CreateWorkerAsync(seed, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await CreateWorkerAsync(seed, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);
        await ApplyGrantAsync(seed.Tenant.Id, quota: 2, CalendarSeed.Now.AddSeconds(3));

        // Room again - a genuine upgrade, distinct from the auto-reactivation
        // RaisingTheQuotaBackUp_DoesNotReactivateAnyoneItPreviouslyDeactivated rules out: this test
        // reactivates through the manual endpoint the author's own ADR names as the intended path.
        await ApplyGrantAsync(seed.Tenant.Id, quota: 3, CalendarSeed.Now.AddSeconds(4));

        var result = await UpdateWorkerAsync(
            seed, third.Value, lastName: "Three", firstName: "C", isActive: true,
            now: CalendarSeed.Now.AddSeconds(5));

        Assert.True(result.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(3, activeCount);
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

    private async Task<Result> UpdateWorkerAsync(
        SeededTenant seed, WorkerId workerId, string lastName, string firstName, bool isActive, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new UpdateWorkerHandler(
            new WorkerRepository(db), new ServiceRepository(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)), new FixedClock(now));

        return await handler.HandleAsync(
            new UpdateWorker(seed.OperatorId, seed.Tenant.Id, workerId, lastName, firstName, null, null, isActive, []),
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
