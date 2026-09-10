using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-88`/`adr/0165`: the first calendar-to-chat crossing, proven end to end on this repository's own
/// side of the boundary - the identical shape <see cref="ModuleQuantityGrantedWireTests"/> already
/// establishes for the opposite direction, reusing its own <see cref="ModuleQuantityGrantedWireFixture"/>
/// (one real Postgres, one real RabbitMQ) rather than paying for a second container pair this suite
/// does not need anything different from.
///
/// <para><b>What is real, what stands in for `ago-chat`, and one honest gap this suite works around -
/// stated plainly, the same split <see cref="ModuleQuantityGrantedWireTests"/>'s own remarks make.</b>
/// The broker is real; the consumer under test (<see cref="ModuleQuantityImpactRequestedConsumer"/>) is
/// this repository's own real, production <c>BackgroundService</c>, and its own real Postgres write
/// (<see cref="WorkerQuotaImpactAnswerer"/> staging the reply on the real <c>outbox</c> table) is real
/// too. What stands in for `ago-chat` on the way in is the `ModuleQuantityImpactRequested` envelope
/// <see cref="PublishRequestAsync"/> builds by hand, byte-identical in shape to what `ago-chat`'s own
/// <c>ModuleQuantityImpactRequestedMapper.ToEnvelope</c> actually produces (this repository's own
/// <see cref="ModuleQuantityImpactRequestedWireContract"/> already asserts the same property names for
/// the identical "default <c>JsonSerializer</c> options, no shared assembly" reason).</para>
///
/// <para><b>`25-44`: the gap this paragraph used to describe (`Ago.Calendar.Worker.OutboxDispatcher`
/// did not exist, so nothing published a staged row to a real broker) is closed - see
/// <see cref="OutboxDispatcherTests"/> for the proof that this exact reply is actually delivered, not
/// only staged. This suite's own scope stays what it always was: the reply this consumer stages lands
/// correctly in the real <c>outbox</c> table - exactly the fact
/// <see cref="WorkerQuotaImpactAnswererTests"/> already proves for a direct call, now proven again
/// reached correctly through a real broker delivery and the real consumer's own deserialize-and-filter
/// logic. The second hop (outbox row to broker message) is <see cref="OutboxDispatcherTests"/>'s own
/// job, not this suite's - each proves its own half of the pipeline once, rather than one suite
/// re-proving the other's.</para>
/// </summary>
[Collection(ModuleQuantityGrantedWireCollection.Name)]
public sealed class ModuleQuantityImpactRequestedWireTests(ModuleQuantityGrantedWireFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private const string RequestTopic = "ModuleQuantityImpactRequested";
    private const string ConsumerName = "calendar-worker-quota-impact-answer";

    [Fact]
    public async Task ARequestPublishedOverTheRealBroker_IsAnsweredByTheRealConsumer_OnThisProductsOwnOutbox()
    {
        var seed = await SeedTenantWithTwoActiveWorkersAsync();
        var correlationId = Guid.NewGuid();

        await RunConsumerAsync(async () =>
        {
            // The real question, over the real wire, exactly the way ago-chat's own outbox dispatcher
            // would have published it.
            await PublishRequestAsync(seed.TenantId, requestedQuantity: 1, correlationId);

            var row = await WaitForOutboxRowAsync(seed.TenantId, TimeSpan.FromSeconds(20));
            var reply = JsonSerializer.Deserialize<ModuleQuantityImpactComputed>(row.Payload)!;

            Assert.Equal(nameof(ModuleQuantityImpactComputed), row.Type);
            Assert.Equal(seed.TenantId.Value.ToString(), row.PartitionKey);
            Assert.Equal(seed.TenantId.Value, reply.SiteId);
            Assert.Equal("calendar", reply.ModuleKey);
            Assert.Equal(1, reply.RequestedQuantity);
            Assert.Equal(1, reply.AffectedCount);
            Assert.Equal([seed.NewestWorkerDisplayName], reply.AffectedItemDisplayNames);
            Assert.Equal(correlationId, reply.CorrelationId);
        });
    }

    [Fact]
    public async Task ARequestForADifferentModule_IsIgnored_AndNothingIsStagedOnTheOutbox()
    {
        var seed = await SeedTenantWithTwoActiveWorkersAsync();

        await RunConsumerAsync(async () =>
        {
            await PublishRequestAsync(
                seed.TenantId, requestedQuantity: 0, Guid.NewGuid(), moduleKey: "some-other-module");

            // Nothing to wait for succeeding - only for the absence to hold up over a real window,
            // the same "prove silence, not merely the lack of an early row" shape a consumer that
            // correctly declines to act deserves (messaging.md: "a consumer that decides to do
            // nothing is not a failure").
            var row = await TryWaitForOutboxRowAsync(seed.TenantId, TimeSpan.FromSeconds(5));
            Assert.Null(row);
        });
    }

    [Fact]
    public async Task TheIdenticalRequest_PublishedTwiceOverTheRealBroker_IsAnsweredTwice_WithTheIdenticalNumbers()
    {
        // The redelivery half of the claim, over the real wire: at-least-once delivery (messaging.md)
        // means the real consumer may see the identical question twice, and this consumer has no
        // inbox ledger (ModuleQuantityImpactRequestedConsumer's own remarks) - both answers must
        // still carry the identical, correct numbers, which is what makes answering twice harmless.
        var seed = await SeedTenantWithTwoActiveWorkersAsync();
        var correlationId = Guid.NewGuid();

        await RunConsumerAsync(async () =>
        {
            await PublishRequestAsync(seed.TenantId, requestedQuantity: 1, correlationId);
            await PublishRequestAsync(seed.TenantId, requestedQuantity: 1, correlationId);

            var rows = await WaitForOutboxRowCountAsync(seed.TenantId, count: 2, TimeSpan.FromSeconds(20));

            var replies = rows.Select(r => JsonSerializer.Deserialize<ModuleQuantityImpactComputed>(r.Payload)!).ToList();
            Assert.All(replies, reply => Assert.Equal(1, reply.AffectedCount));
            Assert.Equal(replies[0].AffectedItemDisplayNames, replies[1].AffectedItemDisplayNames);
            // Two distinct outbox rows - a fresh MessageId (this outbox row's own id) each answer, the
            // identical "the fact is idempotent, not the envelope" distinction
            // ModuleQuantityGrantedConsumer's own remarks make for the opposite direction.
            Assert.NotEqual(rows[0].Id, rows[1].Id);
        });
    }

    // ------------------------------------------------------------------------------------------
    // Wire plumbing - real classes throughout, no fakes.
    // ------------------------------------------------------------------------------------------

    private async Task PublishRequestAsync(
        TenantId tenantId, int requestedQuantity, Guid correlationId, string moduleKey = "calendar")
    {
        await using var connection = CreateRabbitMqConnection();
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);

        var payload = JsonSerializer.Serialize(new
        {
            SiteId = tenantId.Value,
            ModuleKey = moduleKey,
            RequestedQuantity = requestedQuantity,
            CorrelationId = correlationId,
            OccurredAt = Now,
        });

        await publisher.PublishAsync(
            new EventEnvelope(
                MessageId: Guid.NewGuid(),
                Type: RequestTopic,
                Version: 1,
                PartitionKey: tenantId.Value.ToString(),
                OccurredAt: Now,
                CorrelationId: correlationId,
                Payload: payload),
            CancellationToken.None);
    }

    /// <summary>Starts the real <see cref="ModuleQuantityImpactRequestedConsumer"/> - the identical
    /// constructor call <c>Ago.Calendar.Worker</c>'s own DI container makes, reproduced here the same
    /// way <see cref="ModuleQuantityGrantedWireTests.RunConsumerAsync"/> does for its own sibling, and
    /// waits for its own queue to have a live consumer attached before running
    /// <paramref name="afterSubscribed"/> - see that method's own remarks for why publishing any
    /// earlier would lose the message rather than queue it.</summary>
    private async Task RunConsumerAsync(Func<Task> afterSubscribed)
    {
        await using var connection = CreateRabbitMqConnection();
        var consumer = new RabbitMqEventConsumer(connection);

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddDbContext<AgoCalendarDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        // The real adapters ModuleQuantityImpactRequestedConsumer resolves per message - see
        // IWorkerQuotaImpactAnswerer's own remarks for why AnswerAsync commits its own save rather
        // than staging for an inbox call this consumer deliberately does not make.
        services.AddScoped<IOutboxWriter>(provider =>
            new EfOutboxWriter<AgoCalendarDbContext>(provider.GetRequiredService<AgoCalendarDbContext>()));
        services.AddScoped<IWorkerQuotaImpactAnswerer, WorkerQuotaImpactAnswerer>();
        await using var provider = services.BuildServiceProvider();

        var backgroundConsumer = new ModuleQuantityImpactRequestedConsumer(
            consumer,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleQuantityImpactRequestedConsumerOptions()),
            NullLogger<ModuleQuantityImpactRequestedConsumer>.Instance);

        await backgroundConsumer.StartAsync(CancellationToken.None);
        try
        {
            using var management = fixture.CreateRabbitMqManagementClient();
            var landed = await WaitUntilAsync(
                async () => await GetQueueConsumerCountAsync(management, $"{RequestTopic}.{ConsumerName}") is >= 1,
                TimeSpan.FromSeconds(15));
            Assert.True(landed, $"The '{ConsumerName}' subscription to '{RequestTopic}' never attached a live consumer.");

            await afterSubscribed();
        }
        finally
        {
            await backgroundConsumer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<int?> GetQueueConsumerCountAsync(HttpClient management, string queueName)
    {
        using var response = await management.GetAsync($"/api/queues/%2F/{Uri.EscapeDataString(queueName)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.GetProperty("consumer_details").GetArrayLength();
    }

    private RabbitMqConnection CreateRabbitMqConnection() => new(
        Options.Create(new RabbitMqOptions
        {
            HostName = fixture.RabbitMq.Hostname,
            Port = fixture.RabbitMq.GetMappedPublicPort(5672),
            UserName = "ago-test",
            Password = "ago-test-local-dev",
        }),
        NullLogger<RabbitMqConnection>.Instance);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return await condition();
    }

    // ------------------------------------------------------------------------------------------
    // The real consumer's own real write, read back directly from Postgres - see this class's own
    // remarks for why this is where the proof stops rather than continuing onto a second broker hop.
    // ------------------------------------------------------------------------------------------

    private sealed record OutboxRow(Guid Id, string Type, string PartitionKey, string Payload);

    private async Task<OutboxRow> WaitForOutboxRowAsync(TenantId tenantId, TimeSpan timeout)
    {
        var row = await TryWaitForOutboxRowAsync(tenantId, timeout);
        Assert.True(row is not null, $"No reply was staged on the outbox for tenant {tenantId.Value} within {timeout}.");
        return row!;
    }

    private async Task<OutboxRow?> TryWaitForOutboxRowAsync(TenantId tenantId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var rows = await OutboxRowsAsync(tenantId);
            if (rows.Count > 0)
            {
                return rows[0];
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private async Task<IReadOnlyList<OutboxRow>> WaitForOutboxRowCountAsync(TenantId tenantId, int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var rows = await OutboxRowsAsync(tenantId);
            if (rows.Count >= count)
            {
                return rows;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Only {rows.Count}/{count} replies were staged for tenant {tenantId.Value} within {timeout}.");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private async Task<IReadOnlyList<OutboxRow>> OutboxRowsAsync(TenantId tenantId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select id, type, partition_key, payload from outbox
            where partition_key = @partitionKey and type = @type
            order by occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("partitionKey", tenantId.Value.ToString());
        command.Parameters.AddWithValue("type", nameof(ModuleQuantityImpactComputed));

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    // ------------------------------------------------------------------------------------------
    // Seeding - two active workers so a requested quantity of one has exactly one affected worker
    // to name, unambiguously the more recently created of the two.
    // ------------------------------------------------------------------------------------------

    private sealed record SeededTenantWithWorkers(TenantId TenantId, string NewestWorkerDisplayName);

    private async Task<SeededTenantWithWorkers> SeedTenantWithTwoActiveWorkersAsync()
    {
        // Not truncated, unlike some seed helpers elsewhere: TenantPublicKey allows up to 64
        // characters (its own MaxLength), and truncating a fixed-`Now` UUIDv7 down to exactly its
        // 12-hex-char timestamp region (as a `[..24]` cut after a 12-character prefix would) discards
        // every random bit and collides on the very next call - the actual cause of a genuine
        // ux_tenants_public_key collision hit while writing this suite.
        var tenant = Tenant.Register(
            new TenantId(NewId()), "Wire Test Shop", new TenantPublicKey($"wire-impact-{NewId():N}"), Now);
        var older = Domain.Worker.Create(new WorkerId(NewId()), tenant.Id, "Doe", "Alex", null, Now);
        var newer = Domain.Worker.Create(
            new WorkerId(NewId()), tenant.Id, "Roe", "Sam", null, Now.AddSeconds(1));

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Workers.Add(older);
        db.Workers.Add(newer);
        await db.SaveChangesAsync();

        return new SeededTenantWithWorkers(tenant.Id, newer.DisplayName);
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);
}
