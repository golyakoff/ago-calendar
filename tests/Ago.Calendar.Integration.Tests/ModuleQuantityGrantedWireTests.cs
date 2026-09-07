using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Configuration;
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

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-66`: the wire `22-05`'s and `22-07`'s own reports both named, honestly, as unproven - "no test
/// in either repository exercises a real RabbitMQ round trip between chat's outbox publish and the
/// calendar's consumer... the wire between them is not [proven]." This class is that proof, on this
/// repository's own side of the boundary.
///
/// <para><b>What is real and what is a stand-in, stated plainly.</b> The broker is real
/// (<see cref="ModuleQuantityGrantedWireFixture.RabbitMq"/>, a Testcontainers RabbitMQ), the publisher
/// is real (<see cref="RabbitMqEventPublisher"/>, the identical class
/// <c>Ago.Chat.Worker.OutboxDispatcher</c> calls in production - not a fake, not a raw AMQP client
/// written for this test), and the consumer under test is real
/// (<see cref="ModuleQuantityGrantedConsumer"/>, this repository's own production
/// <c>BackgroundService</c>, started and stopped exactly as <c>Ago.Calendar.Worker</c>'s own
/// <c>Program.cs</c> registers it - <see cref="RunConsumerAsync"/>'s own remarks). The one thing this
/// repository genuinely cannot do is run <c>ago-chat</c>'s own process: it is a separate repository,
/// a separate deployable, with its own database (`adr/0093`, `adr/0027`). What stands in for it is the
/// envelope <see cref="PublishGrantAsync"/> builds by hand, byte-identical in shape to what
/// <c>Ago.Chat.Application.Mapping.ModuleQuantityGrantedMapper.ToEnvelope</c> actually produces (this
/// repository's own <see cref="ModuleQuantityGrantedWireContract"/> already asserts the same property
/// names for the identical "default `JsonSerializer` options, no shared assembly" reason) - a
/// hand-built envelope published through the real publisher onto the real broker, for the real
/// consumer to pick up, is the closest a single repository's own test suite can get to the real thing
/// without a second product's own process running alongside it.</para>
///
/// <para>This is a materially harder guarantee than either of the tests it sits beside:
/// <c>WorkerQuotaTests</c> proves the calendar's own enforcement by calling
/// <see cref="IWorkerQuotaGrantStore.ApplyAsync"/> directly, never touching a broker at all;
/// <c>Ago.Chat.Integration.Tests.ModuleQuantityGrantedOutboxTests</c> proves chat's own outbox row and
/// stops there, by that file's own class remarks. Neither, alone or together, proves a message
/// actually crosses the wire and is picked up by a live, subscribed consumer - which is exactly the
/// claim `23-66` exists to make good on.</para>
/// </summary>
[Collection(ModuleQuantityGrantedWireCollection.Name)]
public sealed class ModuleQuantityGrantedWireTests(ModuleQuantityGrantedWireFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The end-to-end claim `23-66`'s own report has to make good on, in one test: a tenant with the
    /// calendar module and no granted quota cannot create a worker; a quantity is granted the exact
    /// way `ago-chat`'s own outbox would grant it - over a real broker, to a real, running consumer,
    /// never applied by calling a store method directly; and only then can the tenant create their
    /// first worker.
    /// </summary>
    [Fact]
    public async Task AGrantPublishedOverTheRealBroker_IsAppliedByTheRealConsumer_AndTheTenantCanThenCreateItsFirstWorker()
    {
        var seed = await SeedBareTenantAsync();

        // Before: WorkerQuota is zero (the default every freshly registered tenant carries -
        // Tenant.Register's own remarks), so the calendar refuses the first worker exactly as it
        // would for any tenant that has not been granted the add-on yet.
        var beforeGrant = await CreateWorkerAsync(seed, "Alex", "Doe", Now);
        Assert.True(beforeGrant.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", beforeGrant.Error!.Value.Code);

        // The real consumer, started and stopped exactly as Ago.Calendar.Worker's own Program.cs
        // registers it - never a store method called directly (RunConsumerAsync's own remarks).
        // The grant is published only once RunConsumerAsync confirms the consumer's own queue is
        // actually bound and attached - see that method's own remarks for why publishing first would
        // lose the message rather than queue it, on this queue's first-ever subscription.
        await RunConsumerAsync(async () =>
        {
            // The grant, over the real wire: a real RabbitMqEventPublisher (the identical class
            // Ago.Chat.Worker.OutboxDispatcher uses) publishes the envelope ago-chat's own outbox
            // dispatcher would have published, onto a real, Testcontainers-backed broker.
            await PublishGrantAsync(seed.TenantId, quantity: 1);

            await WaitUntilAsync(
                async () => await GetWorkerQuotaAsync(seed.TenantId) == 1, TimeSpan.FromSeconds(20));
        });

        var quotaAfterGrant = await GetWorkerQuotaAsync(seed.TenantId);
        Assert.Equal(1, quotaAfterGrant);

        // After: the claim the item exists for. The tenant's first worker, created through the real
        // handler, against the real row the consumer above actually wrote - not asserted from the
        // quota column alone.
        var afterGrant = await CreateWorkerAsync(seed, "Alex", "Doe", Now.AddSeconds(1));
        Assert.True(afterGrant.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var activeCount = await verify.Workers.CountAsync(
            w => w.TenantId == seed.TenantId && w.IsActive, CancellationToken.None);
        Assert.Equal(1, activeCount);
    }

    /// <summary>
    /// The redelivery half of the same claim, over the real wire this time rather than
    /// <c>WorkerQuotaTests.ReapplyingTheIdenticalGrant_IsANoOp_ProvingTheSnapshotIsIdempotent</c>'s
    /// direct store call: at-least-once delivery (`messaging.md`) means the real consumer must see the
    /// identical envelope twice and land on the identical, correct state either way.
    /// </summary>
    [Fact]
    public async Task TheIdenticalGrant_PublishedTwiceOverTheRealBroker_IsANoOpTheSecondTime()
    {
        var seed = await SeedBareTenantAsync();

        await RunConsumerAsync(async () =>
        {
            await PublishGrantAsync(seed.TenantId, quantity: 1);
            // A distinct MessageId each time - a genuine redelivery has a different envelope id but
            // the identical CorrelationId/quantity, the same "the fact is idempotent, not the
            // envelope" distinction ModuleQuantityGrantedConsumer's own remarks make.
            await PublishGrantAsync(seed.TenantId, quantity: 1);

            await WaitUntilAsync(
                async () => await GetWorkerQuotaAsync(seed.TenantId) == 1, TimeSpan.FromSeconds(20));

            // Give the second delivery time to be (harmlessly) reapplied before this method returns
            // and the consumer is stopped.
            await Task.Delay(TimeSpan.FromSeconds(1));
        });

        Assert.Equal(1, await GetWorkerQuotaAsync(seed.TenantId));
    }

    // ------------------------------------------------------------------------------------------
    // Wire plumbing - real classes throughout, no fakes.
    // ------------------------------------------------------------------------------------------

    /// <summary>Builds the envelope the way <c>Ago.Chat.Application.Mapping.ModuleQuantityGrantedMapper.ToEnvelope</c>
    /// does (topic <c>"ModuleQuantityGranted"</c>, version 1, partition key = the site id, a JSON
    /// payload whose property names match <c>Ago.Chat.Contracts.ModuleQuantityGranted</c> under
    /// default <see cref="JsonSerializer"/> options - see this class's own remarks for why this
    /// repository builds it by hand rather than referencing that type), then publishes it through a
    /// real <see cref="RabbitMqEventPublisher"/>.</summary>
    private async Task PublishGrantAsync(TenantId tenantId, int quantity)
    {
        await using var connection = CreateRabbitMqConnection();
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);

        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            SiteId = tenantId.Value,
            ModuleKey = "calendar",
            Quantity = quantity,
            CorrelationId = correlationId,
            OccurredAt = Now,
        });

        await publisher.PublishAsync(
            new EventEnvelope(
                MessageId: Guid.NewGuid(),
                Type: "ModuleQuantityGranted",
                Version: 1,
                PartitionKey: tenantId.Value.ToString(),
                OccurredAt: Now,
                CorrelationId: correlationId,
                Payload: payload),
            CancellationToken.None);
    }

    /// <summary>`ModuleQuantityGrantedConsumer`'s own private <c>ConsumerName</c> - duplicated here,
    /// not shared, the identical "no symbol across the assembly boundary to derive it from" reasoning
    /// <c>Ago.Chat.Integration.Tests.RabbitMqSubscriptionTestHelpers</c>'s own remarks give for its own
    /// topic-name duplication.</summary>
    private const string ConsumerName = "calendar-worker-quota-grant";

    private const string Topic = "ModuleQuantityGranted";

    /// <summary>Starts the real <see cref="ModuleQuantityGrantedConsumer"/> - the identical
    /// constructor call <c>Ago.Calendar.Worker</c>'s own DI container makes, reproduced here rather
    /// than resolved through a full host (this suite needs the one <c>BackgroundService</c>, not
    /// every registration <c>CalendarModule</c> makes for handlers this test never calls - the
    /// identical "each integration suite wires exactly what it needs" posture
    /// <c>Ago.Chat.Integration.Tests.OwnerModuleEndpointsTests</c>'s own remarks state for itself).
    ///
    /// <para><b>Waits for the consumer's own queue to actually have a consumer attached before
    /// invoking <paramref name="afterSubscribed"/>.</b> `RabbitMqEventConsumer.SubscribeAsync` runs
    /// four steps in order (declare queue, bind it, declare the retry/DLQ, `BasicConsumeAsync`), and
    /// <c>BackgroundService.StartAsync</c> returns as soon as that work is *started*, not once it
    /// *completes* - `Ago.Chat.Integration.Tests.RabbitMqSubscriptionTestHelpers`' own remarks name
    /// this exact race and the "0/6" failure it caused there. This queue is a `Competing` queue named
    /// <c>{Topic}.{ConsumerName}</c>, so its own consumer count is polled off the management API
    /// (`ModuleQuantityGrantedWireFixture.CreateRabbitMqManagementClient`) until at least one consumer
    /// is attached - the caller is expected to publish only inside <paramref name="afterSubscribed"/>,
    /// never before this method is called, or the identical message-loss race resurfaces.</para>
    /// </summary>
    private async Task RunConsumerAsync(Func<Task> afterSubscribed)
    {
        await using var connection = CreateRabbitMqConnection();
        var consumer = new RabbitMqEventConsumer(connection);

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddDbContext<AgoCalendarDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        services.AddSingleton<IClock>(new FixedClock(Now));
        // The real adapters ModuleQuantityGrantedConsumer resolves per message - see
        // IWorkerQuotaGrantStore's own remarks for why ApplyAsync manages its own transaction rather
        // than staging for this consumer to combine with the inbox record below.
        services.AddScoped<IWorkerQuotaGrantStore, WorkerQuotaGrantStore>();
        services.AddScoped<IInboxChecker, EfInboxChecker<AgoCalendarDbContext>>();
        await using var provider = services.BuildServiceProvider();

        var backgroundConsumer = new ModuleQuantityGrantedConsumer(
            consumer,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleQuantityGrantedConsumerOptions()),
            NullLogger<ModuleQuantityGrantedConsumer>.Instance);

        await backgroundConsumer.StartAsync(CancellationToken.None);
        try
        {
            using var management = fixture.CreateRabbitMqManagementClient();
            var landed = await WaitUntilAsync(
                async () => await GetQueueConsumerCountAsync(management, $"{Topic}.{ConsumerName}") is >= 1,
                TimeSpan.FromSeconds(15));
            Assert.True(landed, $"The '{ConsumerName}' subscription to '{Topic}' never attached a live consumer.");

            await afterSubscribed();
        }
        finally
        {
            await backgroundConsumer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Reads a queue's own live `consumer_details` length off the RabbitMQ management API -
    /// `null` when the queue does not exist yet, otherwise how many channels are currently
    /// `BasicConsume`-ing on it. The same fact and the same "not the stats-only `consumers` field,
    /// which is genuinely absent for a few seconds after a queue is created" choice
    /// <c>Ago.Chat.Integration.Tests.RabbitMqSubscriptionTestHelpers.GetQueueConsumerCountAsync</c>'s
    /// own remarks make.</summary>
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

    private async Task<int> GetWorkerQuotaAsync(TenantId tenantId)
    {
        await using var db = fixture.CreateDbContext();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, CancellationToken.None);
        return tenant.WorkerQuota;
    }

    /// <summary>Polls <paramref name="condition"/> until it is true or <paramref name="timeout"/>
    /// elapses - this file's own small stand-in for <c>Ago.Chat.Integration.Tests.OutboxTestHelpers</c>,
    /// which this repository has no equivalent of yet (this is the first test here to wait on a
    /// background consumer's own eventual effect rather than a synchronous call's return value).
    /// </summary>
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
    // Seeding and worker creation - the identical shape WorkerQuotaTests already established, minus
    // the pre-seeded worker: this file's whole point is a tenant with none yet.
    // ------------------------------------------------------------------------------------------

    private sealed record SeededBareTenant(TenantId TenantId, CalendarId CalendarId, OperatorId OperatorId);

    private static readonly string[] AllPermissions =
    [
        Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.BookingCancel.Value,
        Permission.BookingMarkNoShow.Value, Permission.CustomerRead.Value, Permission.CustomerEdit.Value,
        Permission.CalendarConfigure.Value,
    ];

    private async Task<SeededBareTenant> SeedBareTenantAsync()
    {
        var tenant = Tenant.Register(
            new TenantId(NewId()), "Wire Test Shop", new TenantPublicKey($"wire-{NewId():N}"[..24]), Now);
        var calendar = BookingCalendar.Create(new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), Now);
        calendar.Publish();

        var subject = $"kc-{NewId():N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);

        var projections = new RoleAssignmentProjectionStore(db);
        await projections.StageAsync(operatorId, tenant.Id, subject, AllPermissions, Now, CancellationToken.None);

        await db.SaveChangesAsync();

        return new SeededBareTenant(tenant.Id, calendar.Id, operatorId);
    }

    private async Task<Result<WorkerId>> CreateWorkerAsync(
        SeededBareTenant seed, string firstName, string lastName, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new CreateWorkerHandler(
            new BookingCalendarRepository(db), new ServiceRepository(db), new WorkerRepository(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)), new ClockIdGenerator(), new FixedClock(now));

        return await handler.HandleAsync(
            new CreateWorker(seed.OperatorId, seed.TenantId, lastName, firstName, null, null, seed.CalendarId, []),
            CancellationToken.None);
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);

    /// <summary>Mints real UUIDv7 ids from the timestamp it is given - the identical private helper
    /// <c>WorkerQuotaTests</c> already declares for the same reason (its own remarks).</summary>
    private sealed class ClockIdGenerator : IIdGenerator
    {
        public Guid NewId(DateTimeOffset now) => Guid.CreateVersion7(now);
    }
}
