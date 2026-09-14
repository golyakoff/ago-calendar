using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.TenantErasure;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `25-82`'s own Done-when, demonstrated against a real Postgres rather than asserted from the
/// design: `role_assignment_projections` for a tenant `ago-chat` has hard-deleted stops accumulating,
/// and the mechanism survives its own message twice - the identical proof shape
/// <see cref="RoleAssignmentProjectionDemonstrationTests"/> already establishes for the sibling
/// consumer, driven the same way: exercised directly at the store/handler seam
/// (<see cref="EraseTenantDataHandler"/>, <see cref="EfInboxChecker{TContext}"/>) rather than over a
/// real broker - this repository has no RabbitMQ container in its fixtures, and the broker round trip
/// is `ago-chat`'s own half of this proof (<c>SiteErasedOutboxTests</c> in that repository).
///
/// <para><b>No <see cref="Tenant"/> row at all - the exact shape `25-82`'s own count found.</b> A demo
/// tenant is chat-only; it never registers this product's own module, so it never gets a
/// <see cref="Tenant"/> row here. What it does get is a <c>role_assignment_projections</c> row, staged
/// unconditionally whenever `ago-chat` publishes <c>RoleAssignmentsChanged</c> for one of its
/// operators - which every seeded founder operator triggers at registration, module or no module. This
/// file seeds only that row, deliberately never writing a <see cref="Tenant"/>, so the claim proven
/// here is the one the item measured: the projection is drained even when nothing else in this product
/// ever knew the tenant existed.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteErasedConsumerDemonstrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The item's own central claim, read literally: a tenant with a `role_assignment_projections` row
    /// and no `tenants` row at all - the demo-tenant shape, not the paid-module shape
    /// <see cref="TenantErasureEndpointTests"/> already covers - loses that row once `SiteErased`
    /// (stood in for here by the same steps <see cref="SiteErasedConsumer"/>'s own `HandleAsync` takes)
    /// is processed for it.
    /// </summary>
    [Fact]
    public async Task ATenantWithOnlyARoleAssignmentProjectionRow_LosesItOnceSiteErasedIsProcessed()
    {
        var tenantId = new TenantId(Guid.CreateVersion7(DateTimeOffset.UtcNow));
        var subject = $"kc-{Guid.CreateVersion7(DateTimeOffset.UtcNow):N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                operatorId, tenantId, subject, CalendarSeed.AllPermissions, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await CountProjectionRowsAsync(tenantId));

        await DeliverSiteErasedAsync(tenantId, Guid.NewGuid());

        Assert.Equal(0, await CountProjectionRowsAsync(tenantId));
        // No Tenant row either - EraseTenantDataHandler's own TenantExisted=false branch, confirmed
        // rather than assumed: this is exactly the "erase everything, whether or not a tenants row was
        // ever written" reuse this item's own report explains.
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Tenants.AnyAsync(t => t.Id == tenantId, CancellationToken.None));
    }

    /// <summary>
    /// `CLAUDE.md` rule 5, at this consumer's own seam: the same message id delivered twice. The first
    /// delivery erases and records the inbox; the second re-attempts the identical erasure (a no-op by
    /// <see cref="ITenantErasureRepository.EraseAsync"/>'s own construction - already exhaustively
    /// proven idempotent in <see cref="TenantErasureEndpointTests"/>) and then loses its own inbox
    /// insert to the same <c>(message_id, consumer)</c> unique index. What "nothing doubles" means
    /// here: zero projection rows and exactly one inbox row, after two deliveries of the same fact.
    /// </summary>
    [Fact]
    public async Task TheSameSiteErasedMessageDeliveredTwice_ErasesOnceAndRecordsTheInboxOnce()
    {
        var tenantId = new TenantId(Guid.CreateVersion7(DateTimeOffset.UtcNow));
        var subject = $"kc-{Guid.CreateVersion7(DateTimeOffset.UtcNow):N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);
        var messageId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                operatorId, tenantId, subject, CalendarSeed.AllPermissions, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var firstDeliveryWasNew = await DeliverSiteErasedAsync(tenantId, messageId);
        var secondDeliveryWasNew = await DeliverSiteErasedAsync(tenantId, messageId);

        Assert.True(firstDeliveryWasNew);
        Assert.False(secondDeliveryWasNew);

        Assert.Equal(0, await CountProjectionRowsAsync(tenantId));

        await using var reader = fixture.CreateDbContext();
        var inboxRowCount = await reader.Set<InboxRecord>()
            .CountAsync(r => r.MessageId == messageId && r.Consumer == ConsumerName, CancellationToken.None);
        Assert.Equal(1, inboxRowCount);
    }

    private const string ConsumerName = "calendar-tenant-erasure";

    /// <summary>
    /// The two steps <see cref="SiteErasedConsumer.HandleAsync"/> takes once it has deserialized the
    /// envelope, replayed directly - that method itself is private and this repository has no broker
    /// fixture to drive it through (this class's own remarks), the identical seam
    /// <see cref="RoleAssignmentProjectionDemonstrationTests.DeliverOnceAsync"/> already establishes for
    /// the sibling consumer.
    /// </summary>
    private async Task<bool> DeliverSiteErasedAsync(TenantId tenantId, Guid messageId)
    {
        await using var db = fixture.CreateDbContext();
        var erasure = new EraseTenantDataHandler(new TenantErasureRepository(db));
        var inbox = new EfInboxChecker<AgoCalendarDbContext>(db, new FixedClock(Now));

        await erasure.HandleAsync(new EraseTenantData(tenantId), CancellationToken.None);

        return await inbox.TryRecordAndSaveAsync(messageId, ConsumerName, CancellationToken.None);
    }

    /// <summary>Raw SQL against the real Postgres, not the EF-mapped record type -
    /// <see cref="RoleAssignmentProjectionRecord"/> is `internal` to
    /// <c>Ago.Calendar.Infrastructure.Postgres</c>, and reading through <c>IRoleAssignmentProjectionStore</c>
    /// (a permission set, not a row count) would not distinguish "no row" from "a row with an empty
    /// permission set" - the same distinction <see cref="TenantErasureEndpointTests.CountAsync"/> avoids
    /// for the identical reason, in this same repository.</summary>
    private async Task<int> CountProjectionRowsAsync(TenantId tenantId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from role_assignment_projections where tenant_id = @tenantId",
            new { tenantId = tenantId.Value });
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
