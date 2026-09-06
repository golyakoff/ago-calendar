using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-12`/`adr/0123`'s own Done-when, demonstrated against a real Postgres rather than asserted from
/// the design: a tenant with no projected rung reads as <see cref="ContactVisibility.Visible"/>, and
/// the consumer that populates the projection survives its own message twice - the identical shape
/// <c>RoleAssignmentProjectionDemonstrationTests</c> already proves for the role catalogue's own
/// projection.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactVisibilityProjectionDemonstrationTests(PostgresFixture fixture)
{
    /// <summary>The item's own explicit Scope clause: "a tenant with no projected value behaves as
    /// Visible" - proved here against a real row that has never been written, not merely against a
    /// fake that happens to default the same way.</summary>
    [Fact]
    public async Task ATenantWithNoProjectedRung_ReadsAsVisible()
    {
        var tenant = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var store = new ContactVisibilityProjectionStore(db);

        var rung = await store.GetAsync(tenant.Tenant.Id, CancellationToken.None);

        Assert.Equal(ContactVisibility.Visible, rung);
    }

    /// <summary>
    /// `ContactVisibilityChangedConsumer`'s own one-save idempotency, exercised directly rather than
    /// over a real broker (this repository has no RabbitMQ container in its fixtures - the broker
    /// round trip is `ago-chat`'s half of this proof). Same <see cref="Guid"/> message id delivered
    /// twice: the first delivery stages and commits with the inbox row; the second stages the
    /// identical value again and then loses the inbox insert to the same <c>(message_id, consumer)</c>
    /// unique index, rolling its own redundant stage back with it. What "nothing doubles" means here:
    /// one projected rung, one inbox row, after two deliveries of the same fact.
    /// </summary>
    [Fact]
    public async Task TheSameRungEventDeliveredTwice_ProjectsOnceAndRecordsTheInboxOnce()
    {
        var tenant = await CalendarSeed.WriteAsync(fixture);
        var messageId = Guid.NewGuid();
        const string consumerName = "calendar-contact-visibility-projection";

        async Task<bool> DeliverOnceAsync()
        {
            await using var db = fixture.CreateDbContext();
            var visibility = new ContactVisibilityProjectionStore(db);
            var inbox = new EfInboxChecker<AgoCalendarDbContext>(db, new FixedClock(CalendarSeed.Now));

            await visibility.StageAsync(
                tenant.Tenant.Id, ContactVisibility.MaskedWithReveal, CalendarSeed.Now, CancellationToken.None);

            return await inbox.TryRecordAndSaveAsync(messageId, consumerName, CancellationToken.None);
        }

        var firstDeliveryWasNew = await DeliverOnceAsync();
        var secondDeliveryWasNew = await DeliverOnceAsync();

        Assert.True(firstDeliveryWasNew);
        Assert.False(secondDeliveryWasNew);

        await using var reader = fixture.CreateDbContext();
        var visibility2 = new ContactVisibilityProjectionStore(reader);
        var rung = await visibility2.GetAsync(tenant.Tenant.Id, CancellationToken.None);
        Assert.Equal(ContactVisibility.MaskedWithReveal, rung);

        // ContactVisibilityProjectionRecord is internal to Ago.Calendar.Infrastructure.Postgres (the
        // same visibility RoleAssignmentProjectionRecord already has), so the row count is proved
        // through a raw count query rather than the EF-mapped type this test project cannot name.
        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from contact_visibility_projections where tenant_id = @tenantId";
        command.Parameters.AddWithValue("tenantId", tenant.Tenant.Id.Value);
        var projectionRowCount = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, projectionRowCount);

        var inboxRowCount = await reader.Set<InboxRecord>()
            .CountAsync(r => r.MessageId == messageId && r.Consumer == consumerName, CancellationToken.None);
        Assert.Equal(1, inboxRowCount);
    }

    /// <summary>A restaged rung replaces the prior value rather than merging with it - the same
    /// full-replace guarantee <c>RoleAssignmentProjectionDemonstrationTests</c> proves for permissions,
    /// here proved for a single-valued fact instead of a set.</summary>
    [Fact]
    public async Task RestagingADifferentRung_ReplacesTheProjectedValue()
    {
        var tenant = await CalendarSeed.WriteAsync(fixture);

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactVisibilityProjectionStore(db);
            await store.StageAsync(tenant.Tenant.Id, ContactVisibility.MaskedWithReveal, CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactVisibilityProjectionStore(db);
            await store.StageAsync(tenant.Tenant.Id, ContactVisibility.Visible, CalendarSeed.Now.AddSeconds(1), CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var rung = await new ContactVisibilityProjectionStore(reader).GetAsync(tenant.Tenant.Id, CancellationToken.None);
        Assert.Equal(ContactVisibility.Visible, rung);
    }
}
