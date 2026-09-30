using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-88`/`adr/0165`'s own read-only reply, against a real Postgres: which of the tenant's own active
/// workers a candidate quantity would exceed, in the identical order the real downgrade
/// (<see cref="WorkerQuotaGrantStore"/>) would deactivate them, staged onto this product's own outbox
/// - never applied, and never touching a worker or tenant row.
///
/// <para>Every fixture seeds through <see cref="CalendarSeed.WriteAsync"/>, which already writes one
/// active worker directly - the counts below always account for that seeded worker rather than
/// pretending the tenant starts empty, the same convention <c>WorkerQuotaTests</c> already
/// establishes.</para>
///
/// <para>`26-317`: the candidate quantity fed to the policy call is really
/// <c>Math.Max(requestedQuantity, Tenant.DefaultWorkerQuota)</c>, not the raw number - the identical
/// floor <c>WorkerQuotaGrantStore.ApplyAsync</c> applies to the real downgrade
/// (<see cref="Tenant.EffectiveWorkerQuota"/>'s own remarks). Several tests below seed a wider roster
/// than before purely to stay clear of that floor and exercise the ordinary "who is excess" rule on
/// its own terms; the floor's own effect on this preview has its own test at the bottom of this
/// class.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerQuotaImpactAnswererTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ARequestedQuantity_AtOrAboveTheActiveCount_AffectsNoWorkers()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 1, correlationId: Guid.NewGuid());

        Assert.Equal(0, reply.AffectedCount);
        Assert.Empty(reply.AffectedItemDisplayNames);
    }

    [Fact]
    public async Task ARequestedQuantity_BelowTheActiveCount_NamesTheNewestWorkersFirst_AndLeavesEveryRowUntouched()
    {
        // `26-317`: four active workers, not three as before - a requested quantity of one floors to
        // Tenant.DefaultWorkerQuota (2), so the roster is widened by one to keep an excess of two and
        // exercise the ordinary "newest first" order on its own terms.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var second = await AddActiveWorkerAsync(seed.Tenant.Id, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await AddActiveWorkerAsync(seed.Tenant.Id, "Three", "C", CalendarSeed.Now.AddSeconds(2));
        var fourth = await AddActiveWorkerAsync(seed.Tenant.Id, "Four", "D", CalendarSeed.Now.AddSeconds(3));

        // Four active workers (the seeded one plus these three); a candidate quantity of one is
        // floored to two before the excess is computed, naming the two most recently created, newest
        // first - the identical order WorkerQuotaPolicy's own deactivation would use, proven directly
        // in WorkerQuotaPolicyTests and reused here rather than re-derived (IWorkerQuotaImpactAnswerer's
        // own remarks).
        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 1, correlationId: Guid.NewGuid());

        Assert.Equal(2, reply.AffectedCount);
        Assert.Equal([fourth.DisplayName, third.DisplayName], reply.AffectedItemDisplayNames);

        // Nothing was written to Workers or Tenants - this call answers, it never applies.
        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(4, activeCount);
        var tenant = await verify.Tenants.SingleAsync(t => t.Id == seed.Tenant.Id, CancellationToken.None);
        Assert.Equal(0, tenant.WorkerQuota);
    }

    [Fact]
    public async Task AnInactiveWorker_IsNeverCounted_NorNamed()
    {
        // `26-317`: two more active workers than before, so three active workers remain once the
        // deactivated one is excluded - enough to leave a real excess of one past the default floor of
        // two, rather than the floor absorbing it and proving nothing.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await AddActiveWorkerAsync(seed.Tenant.Id, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var inactive = await AddActiveWorkerAsync(seed.Tenant.Id, "Gone", "D", CalendarSeed.Now.AddSeconds(2));
        var fourth = await AddActiveWorkerAsync(seed.Tenant.Id, "Four", "E", CalendarSeed.Now.AddSeconds(3));

        await using (var db = fixture.CreateDbContext())
        {
            var worker = await db.Workers.SingleAsync(w => w.Id == inactive.Id, CancellationToken.None);
            worker.Deactivate(CalendarSeed.Now.AddSeconds(4));
            await db.SaveChangesAsync();
        }

        // Three active workers remain (seed, second, fourth); a candidate quantity of zero floors to
        // the default floor of two, naming exactly the one newest ACTIVE worker - never the
        // already-inactive one, even though it was created more recently than "second".
        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 0, correlationId: Guid.NewGuid());

        Assert.Equal(1, reply.AffectedCount);
        Assert.Equal([fourth.DisplayName], reply.AffectedItemDisplayNames);
    }

    /// <summary>`26-317`'s own preview-side Done-when: a requested quantity below the default floor is
    /// floored before it ever reaches <see cref="WorkerQuotaPolicy"/>, so the preview an owner sees
    /// matches what <c>WorkerQuotaGrantStore.ApplyAsync</c> would actually do to the same roster -
    /// never naming a worker the real grant would leave standing.</summary>
    [Fact]
    public async Task ARequestedQuantityBelowTheFloor_IsFlooredBeforeComputingTheImpact()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await AddActiveWorkerAsync(seed.Tenant.Id, "Two", "B", CalendarSeed.Now.AddSeconds(1));

        // Two active workers total; a requested quantity of zero floors to Tenant.DefaultWorkerQuota
        // (2), so nobody is excess - the same answer a request of exactly 2 would give.
        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 0, correlationId: Guid.NewGuid());

        Assert.Equal(0, reply.AffectedCount);
        Assert.Empty(reply.AffectedItemDisplayNames);
        // The echoed quantity is the raw number the owner typed, not the floored one used internally -
        // ModuleQuantityImpactComputedMapper's own remarks: "echoed back from the question, unchanged".
        Assert.Equal(0, reply.RequestedQuantity);
    }

    [Fact]
    public async Task TheReply_CarriesTheEchoedSiteModuleAndQuantity_AndTheThreadedCorrelationId()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var correlationId = Guid.NewGuid();

        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 0, correlationId);

        Assert.Equal(seed.Tenant.Id.Value, reply.SiteId);
        Assert.Equal("calendar", reply.ModuleKey);
        Assert.Equal(0, reply.RequestedQuantity);
        Assert.Equal(correlationId, reply.CorrelationId);
    }

    [Fact]
    public async Task TheReply_IsStagedOnThisProductsOwnOutbox_ToTheLiteralTopic_PartitionedBySite()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await AnswerAsync(seed.Tenant.Id, requestedQuantity: 0, correlationId: Guid.NewGuid());

        var row = Assert.Single(await OutboxRowsAsync(seed.Tenant.Id));
        Assert.Equal(nameof(ModuleQuantityImpactComputed), row.Type);
        Assert.Equal(seed.Tenant.Id.Value.ToString(), row.PartitionKey);
    }

    private async Task<ModuleQuantityImpactComputed> AnswerAsync(
        TenantId tenantId, int requestedQuantity, Guid correlationId)
    {
        await using var db = fixture.CreateDbContext();
        var answerer = new WorkerQuotaImpactAnswerer(
            db, new EfOutboxWriter<Infrastructure.Postgres.Persistence.AgoCalendarDbContext>(db), new UuidV7Generator());

        await answerer.AnswerAsync(tenantId, requestedQuantity, correlationId, CalendarSeed.Now, CancellationToken.None);

        var row = Assert.Single(await OutboxRowsAsync(tenantId));
        return JsonSerializer.Deserialize<ModuleQuantityImpactComputed>(row.Payload)!;
    }

    private async Task<Domain.Worker> AddActiveWorkerAsync(
        TenantId tenantId, string firstName, string lastName, DateTimeOffset now)
    {
        var worker = Domain.Worker.Create(new WorkerId(CalendarSeed.NewId()), tenantId, lastName, firstName, null, now);

        await using var db = fixture.CreateDbContext();
        db.Workers.Add(worker);
        await db.SaveChangesAsync();

        return worker;
    }

    private async Task<IReadOnlyList<OutboxRow>> OutboxRowsAsync(TenantId tenantId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select type, partition_key, payload from outbox where partition_key = @partitionKey order by occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("partitionKey", tenantId.Value.ToString());

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private sealed record OutboxRow(string Type, string PartitionKey, string Payload);
}
