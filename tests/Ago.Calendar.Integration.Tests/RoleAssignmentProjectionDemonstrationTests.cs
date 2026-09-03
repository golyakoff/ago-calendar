using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-05`/`adr/0093`'s own Done-when, each one demonstrated against a real Postgres rather than
/// asserted from the design: an operator acts with no row in a calendar-owned identity table, a
/// revocation stops them, and the consumer that populates the projection survives its own message
/// twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleAssignmentProjectionDemonstrationTests(PostgresFixture fixture)
{
    /// <summary>
    /// The item's whole promise, read literally: this product's schema holds no `operators`, no
    /// `roles`, no `operator_roles` table at all - not "an empty one" - and a real console request
    /// from a subject with only a projection row still succeeds.
    /// </summary>
    [Fact]
    public async Task APersonGrantedCalendarConfigure_ActsInTheCalendar_WithNoRowInAnyCalendarOwnedIdentityTable()
    {
        await using var schema = fixture.CreateDbContext();
        Assert.False(await TableExistsAsync(schema, "operators"));
        Assert.False(await TableExistsAsync(schema, "roles"));
        Assert.False(await TableExistsAsync(schema, "operator_roles"));
        Assert.True(await TableExistsAsync(schema, "role_assignment_projections"));

        var tenant = await CalendarSeed.WriteAsync(fixture);
        var subject = $"kc-{Guid.CreateVersion7(DateTimeOffset.UtcNow):N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            // Exactly one permission - the item's own example - not the seed's full v1 set, so this
            // proves the specific grant rather than "any row at all happens to work".
            await projections.StageAsync(
                operatorId, tenant.Tenant.Id, subject, [Permission.CalendarConfigure.Value],
                CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/console/configuration/allowed-origins");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        request.Content = JsonContent.Create(new SetAllowedOriginsRequest(["https://shop.example.com"]));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Revocation is the same fact becoming empty - staged and committed exactly like a grant, through
    /// the identical <see cref="IRoleAssignmentProjectionStore"/> a real `RoleAssignmentsChanged`
    /// delivery would use. The staleness bound this proves is zero *from the moment the projection
    /// commits* - the request immediately after a revocation lands is refused, with no cache and no
    /// window of its own to wait out. The bound this item's report states is therefore entirely the
    /// upstream pipeline's own latency (outbox dispatch poll interval plus one broker hop), not
    /// anything this check adds.
    /// </summary>
    [Fact]
    public async Task RevokingThePermission_RefusesTheirVeryNextRequest_NoWindowOfItsOwn()
    {
        var tenant = await CalendarSeed.WriteAsync(fixture);
        var subject = $"kc-{Guid.CreateVersion7(DateTimeOffset.UtcNow):N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                operatorId, tenant.Tenant.Id, subject, [Permission.CalendarConfigure.Value],
                CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using (var beforeRevocation = new HttpRequestMessage(HttpMethod.Put, "/api/v1/console/configuration/allowed-origins"))
        {
            beforeRevocation.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
            beforeRevocation.Content = JsonContent.Create(new SetAllowedOriginsRequest(["https://shop.example.com"]));
            var response = await client.SendAsync(beforeRevocation);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        // The revocation: the same fact, restaged as empty - exactly what RemoveOperatorHandler and
        // RoleAssignmentsChangedConsumer do together in production, replayed here at the one seam
        // this repository can reach directly (this is ago-calendar; ago-chat's own half of the proof
        // is RoleAssignmentsChangedOutboxTests in that repository).
        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                operatorId, tenant.Tenant.Id, subject, [], CalendarSeed.Now.AddSeconds(1), CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using var afterRevocation = new HttpRequestMessage(HttpMethod.Put, "/api/v1/console/configuration/allowed-origins");
        afterRevocation.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        afterRevocation.Content = JsonContent.Create(new SetAllowedOriginsRequest(["https://shop.example.com"]));
        var refusal = await client.SendAsync(afterRevocation);

        Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
    }

    /// <summary>
    /// `RoleAssignmentsChangedConsumer`'s own two-defence idempotency, exercised directly rather than
    /// over a real broker (this repository has no RabbitMQ container in its fixtures - the broker
    /// round trip is `ago-chat`'s half of this proof, `OperatorRemovalEndToEndTests`'s own shape,
    /// applied to a topic this product consumes instead of one it publishes). Same
    /// <see cref="Guid"/> message id delivered twice: the first delivery stages and commits with the
    /// inbox row; the second stages the identical values again and then loses the inbox insert to the
    /// same <c>(message_id, consumer)</c> unique index, rolling its own redundant stage back with it -
    /// <see cref="IInboxChecker"/>'s own documented behaviour. What "nothing doubles" means here: one
    /// projection row, one inbox row, after two deliveries of the same fact.
    /// </summary>
    [Fact]
    public async Task TheSameMessageDeliveredTwice_ProjectsOnceAndRecordsTheInboxOnce()
    {
        var tenant = await CalendarSeed.WriteAsync(fixture);
        var subject = $"kc-{Guid.CreateVersion7(DateTimeOffset.UtcNow):N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);
        var messageId = Guid.NewGuid();
        const string consumerName = "calendar-role-assignment-projection";

        async Task<bool> DeliverOnceAsync()
        {
            await using var db = fixture.CreateDbContext();
            var projections = new RoleAssignmentProjectionStore(db);
            var inbox = new EfInboxChecker<AgoCalendarDbContext>(db, new FixedClock(CalendarSeed.Now));

            await projections.StageAsync(
                operatorId, tenant.Tenant.Id, subject, [Permission.CalendarConfigure.Value],
                CalendarSeed.Now, CancellationToken.None);

            return await inbox.TryRecordAndSaveAsync(messageId, consumerName, CancellationToken.None);
        }

        var firstDeliveryWasNew = await DeliverOnceAsync();
        var secondDeliveryWasNew = await DeliverOnceAsync();

        Assert.True(firstDeliveryWasNew);
        Assert.False(secondDeliveryWasNew);

        await using var reader = fixture.CreateDbContext();
        var projections2 = new RoleAssignmentProjectionStore(reader);
        var permissions = await projections2.GetPermissionsAsync(operatorId, tenant.Tenant.Id, CancellationToken.None);
        Assert.Single(permissions);

        var inboxRowCount = await reader.Set<InboxRecord>()
            .CountAsync(r => r.MessageId == messageId && r.Consumer == consumerName, CancellationToken.None);
        Assert.Equal(1, inboxRowCount);
    }

    private static async Task<bool> TableExistsAsync(AgoCalendarDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = $"public.{table}";
        command.Parameters.Add(parameter);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
