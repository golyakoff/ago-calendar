using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>
/// `22-07`'s own Done-when, forced rather than hoped for - the same three-part shape
/// <see cref="ConcurrentBookingTests"/> already establishes for a different compare-and-set: every
/// caller on its own pooled connection, all parked on one shared gate opened together, connections
/// opened before the gate so the statement itself is what races.
///
/// <para>Calls <see cref="WorkerRepository.TryAddWithinQuotaAsync"/> directly rather than through
/// <c>CreateWorkerHandler</c> - the identical choice <see cref="ConcurrentBookingTests"/> makes
/// against <c>BookingStore</c> rather than <c>BookEventHandler</c>: what is under test is the
/// adapter's own lock-and-count statement, not the handler's permission check ahead of it.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public class ConcurrentWorkerQuotaTests(ConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(24)]
    public async Task ManyConcurrentWorkerCreations_AgainstAQuotaOfOne_ProduceExactlyOneActiveWorker(int callers)
    {
        var tenantId = await SeedTenantAsync(quota: 1);

        var results = await RaceAsync(callers, tenantId);

        // Exactly one, at every degree of contention - the identical bar ConcurrentBookingTests
        // holds its own compare-and-set to. Two would mean the (N+1)-th worker was not actually
        // refused under load; zero would mean the lock rejects everybody.
        Assert.Single(results, accepted => accepted);
        Assert.Equal(callers - 1, results.Count(accepted => !accepted));

        await using var db = fixture.CreateDbContext();
        var activeCount = await db.Workers.CountAsync(w => w.TenantId == tenantId && w.IsActive);
        Assert.Equal(1, activeCount);
    }

    /// <summary>
    /// The other race this design has to survive, and the reason
    /// <see cref="Ago.Calendar.Infrastructure.Postgres.WorkerQuotaGrantStore"/> locks the tenant row
    /// before making any decision, exactly like <see cref="WorkerRepository.TryAddWithinQuotaAsync"/>
    /// does: a grant lowering the quota, arriving at the same instant as a worker-creation attempt.
    /// Whichever wins the lock, the row-lock ordering guarantees the loser re-reads the winner's own
    /// result rather than an earlier value - so the invariant this test checks is not "which one won"
    /// (either serialization is a legal outcome) but that the two never disagree: the tenant's active
    /// headcount is never left above its own granted quota.
    /// </summary>
    [Fact]
    public async Task AGrantLoweringTheQuota_RacingACreateAttempt_NeverLeavesTheActiveCountAboveTheFinalQuota()
    {
        var tenantId = await SeedTenantAsync(quota: 2);
        await SeedActiveWorkerAsync(tenantId, "Existing", "One");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var createAttempt = Task.Run(() => AttemptAsync(tenantId, index: 99, gate));
        var lowerGrant = Task.Run(async () =>
        {
            await using var db = fixture.CreateDbContext();
            await db.Database.OpenConnectionAsync();
            var quotaGrants = new WorkerQuotaGrantStore(db);
            await gate.Task;
            await quotaGrants.ApplyAsync(tenantId, quota: 1, Now.AddSeconds(1), CancellationToken.None);
        });

        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(createAttempt, lowerGrant);

        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(w => w.TenantId == tenantId && w.IsActive);
        var quota = (await verify.Tenants.SingleAsync(t => t.Id == tenantId)).WorkerQuota;

        Assert.True(
            activeCount <= quota,
            $"active worker count {activeCount} exceeded the granted quota {quota} - the two writers disagreed.");
    }

    private async Task SeedActiveWorkerAsync(TenantId tenantId, string lastName, string firstName)
    {
        var worker = Worker.Create(new WorkerId(NewId()), tenantId, lastName, firstName, null, Now);
        await using var db = fixture.CreateDbContext();
        db.Workers.Add(worker);
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<bool>> RaceAsync(int callers, TenantId tenantId)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, callers)
            .Select(index => Task.Run(() => AttemptAsync(tenantId, index, gate)))
            .ToList();

        // Give every caller a moment to reach the gate before opening it - ConcurrentBookingTests'
        // own remarks on why, restated here rather than only there.
        await Task.Delay(50);
        gate.SetResult();

        return await Task.WhenAll(attempts);
    }

    private async Task<bool> AttemptAsync(TenantId tenantId, int index, TaskCompletionSource gate)
    {
        await using var db = fixture.CreateDbContext();

        // Open the connection before waiting on the gate - a handshake after release would stagger
        // the arrivals by however long each one took to connect.
        await db.Database.OpenConnectionAsync();

        var repository = new WorkerRepository(db);
        var worker = Worker.Create(
            new WorkerId(Guid.CreateVersion7(Now)), tenantId, $"Last{index}", $"First{index}", null, Now);

        await gate.Task;

        return await repository.TryAddWithinQuotaAsync(worker, CancellationToken.None);
    }

    private async Task<TenantId> SeedTenantAsync(int quota)
    {
        var tenant = Tenant.Register(
            new TenantId(NewId()), "Barbershop", new TenantPublicKey("shop-" + NewId().ToString("N")), Now);
        tenant.GrantWorkerQuota(quota);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant.Id;
    }

    private static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
