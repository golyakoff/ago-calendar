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
        var seed = await CalendarSeed.WriteAsync(fixture);
        var second = await AddActiveWorkerAsync(seed.Tenant.Id, "Two", "B", CalendarSeed.Now.AddSeconds(1));
        var third = await AddActiveWorkerAsync(seed.Tenant.Id, "Three", "C", CalendarSeed.Now.AddSeconds(2));

        // Three active workers (the seeded one plus these two); a candidate quota of one names the
        // two most recently created, newest first - the identical order WorkerQuotaPolicy's own
        // deactivation would use, proven directly in WorkerQuotaPolicyTests and reused here rather
        // than re-derived (IWorkerQuotaImpactAnswerer's own remarks).
        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 1, correlationId: Guid.NewGuid());

        Assert.Equal(2, reply.AffectedCount);
        Assert.Equal([third.DisplayName, second.DisplayName], reply.AffectedItemDisplayNames);

        // Nothing was written to Workers or Tenants - this call answers, it never applies.
        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.Tenant.Id && w.IsActive, CancellationToken.None);
        Assert.Equal(3, activeCount);
        var tenant = await verify.Tenants.SingleAsync(t => t.Id == seed.Tenant.Id, CancellationToken.None);
        Assert.Equal(0, tenant.WorkerQuota);
    }

    [Fact]
    public async Task AnInactiveWorker_IsNeverCounted_NorNamed()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var inactive = await AddActiveWorkerAsync(seed.Tenant.Id, "Gone", "D", CalendarSeed.Now.AddSeconds(1));

        await using (var db = fixture.CreateDbContext())
        {
            var worker = await db.Workers.SingleAsync(w => w.Id == inactive.Id, CancellationToken.None);
            worker.Deactivate(CalendarSeed.Now.AddSeconds(2));
            await db.SaveChangesAsync();
        }

        // Only the seeded worker is left active; a candidate quota of zero names exactly that one,
        // never the already-inactive one.
        var reply = await AnswerAsync(seed.Tenant.Id, requestedQuantity: 0, correlationId: Guid.NewGuid());

        Assert.Equal(1, reply.AffectedCount);
        Assert.Equal([seed.Worker.DisplayName], reply.AffectedItemDisplayNames);
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
