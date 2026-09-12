using System.Collections.Concurrent;
using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Realtime;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.ResolveBookingPendingDelivery;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `25-63`'s own demonstration, not an assertion: the real outbox-to-push path, exercised end to
/// end, the identical discipline <c>Ago.Chat.Integration.Tests.ConnectionFanoutEndToEndTests</c>
/// already establishes for `ago-chat`'s own hub - real Postgres, real RabbitMQ, real Redis, a
/// hand-written fake only for the one port genuinely outside this process's own code
/// (<see cref="ILocalConnectionDispatcher"/>, the SignalR-facing edge <c>Ago.Calendar.Api</c> owns).
///
/// <para>The write under test is the real <see cref="RejectBookingHandler"/> against a real
/// <c>PendingConfirmation</c> row - proving the whole chain <c>RejectBookingHandler</c> -&gt; outbox
/// -&gt; <see cref="OutboxDispatcher"/> -&gt; <see cref="BookingPendingFanoutConsumer"/> -&gt;
/// <see cref="ResolveBookingPendingDeliveryTargetsHandler"/> -&gt; <see cref="NodeFanoutPublisher"/>
/// -&gt; <see cref="NodeDeliveryConsumer"/> reaches the operator's own connection, registered under
/// <see cref="PrincipalKeys.ForTenant"/> exactly the way <see cref="Api.Hubs.CalendarOperatorHub"/>
/// registers a real one on connect.</para>
/// </summary>
[Collection(BookingPendingFanoutCollection.Name)]
public sealed class BookingPendingFanoutEndToEndTests(BookingPendingFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    // Hardcoded, not read from BookingPendingFanoutConsumer.ConsumerName - that class's own doc
    // comment explains why: an InternalsVisibleTo grant here would make Ago.Calendar.Worker's and
    // Ago.Calendar.Api's own top-level-statement `Program` classes ambiguous (both live in the global
    // namespace), the same trade this project's own ModuleQuantityGrantedWireTests/
    // ModuleQuantityImpactRequestedWireTests already made for their own consumer names.
    private const string ConsumerName = "booking-pending-fanout";

    [Fact]
    public async Task ABookingLeavingPending_PushedThroughTheRealHandlerChain_ReachesTheOperatorsConnectionOnItsOwnNode()
    {
        var seed = await SeedTenantAsync();
        var booking = await APendingBookingAsync(seed);

        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var otherNode = new NodeId($"node-{Guid.NewGuid():N}");
        var operatorConnection = new ConnectionId(Guid.NewGuid().ToString());
        var otherNodeConnection = new ConnectionId(Guid.NewGuid().ToString());
        await registry.RegisterAsync(operatorConnection, node, PrincipalKeys.ForTenant(seed.Tenant.Id), CancellationToken.None);
        // A second connection for the *same* tenant, registered on a *different* node - proves the
        // fan-out actually groups by node and reaches both, not only the one this test happens to
        // check first (the identical "more than one node" shape ConnectionFanoutEndToEndTests uses).
        await registry.RegisterAsync(otherNodeConnection, otherNode, PrincipalKeys.ForTenant(seed.Tenant.Id), CancellationToken.None);

        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var services = BuildServiceProvider(fanoutPublisherConnection);

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new FixedClock(Now),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2) }), NullLogger<OutboxDispatcher>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new BookingPendingFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BookingPendingFanoutConsumerOptions()), NullLogger<BookingPendingFanoutConsumer>.Instance);

        var localDispatcher = new FakeLocalConnectionDispatcher();
        var otherNodeDispatcher = new FakeLocalConnectionDispatcher();
        await using var nodeConsumerConnection = fixture.CreateRabbitMqConnection();
        await using var otherNodeConsumerConnection = fixture.CreateRabbitMqConnection();
        var nodeConsumer = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnection), localDispatcher, node, NullLogger<NodeDeliveryConsumer>.Instance);
        var otherNodeConsumer = new NodeDeliveryConsumer(new RabbitMqEventConsumer(otherNodeConsumerConnection), otherNodeDispatcher, otherNode, NullLogger<NodeDeliveryConsumer>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumer.StartAsync(CancellationToken.None);
        await otherNodeConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for each Competing subscription's own queue to have a live consumer attached
        // before publishing anything - see ModuleQuantityImpactRequestedWireTests' own remarks for
        // why the queue merely existing is not enough.
        using var management = fixture.CreateRabbitMqManagementClient();
        await AwaitCompetingSubscriptionsAsync(
            management, TimeSpan.FromSeconds(15),
            (nameof(BookingPendingStateChanged), ConsumerName),
            (NodeTopics.For(node), "node-delivery"),
            (NodeTopics.For(otherNode), "node-delivery"));

        try
        {
            await using var writeDb = fixture.CreateDbContext();
            var handler = new RejectBookingHandler(
                new EventRepository(writeDb), new AllowingPermissionChecker(),
                new EfOutboxWriter<AgoCalendarDbContext>(writeDb), new UuidV7Generator(), new FixedClock(Now));

            var result = await handler.HandleAsync(
                new RejectBooking(new OperatorId(Guid.NewGuid()), seed.Tenant.Id, booking.Id), CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var delivered = await WaitUntilAsync(() => localDispatcher.Dispatches.Count > 0, TimeSpan.FromSeconds(20));
            Assert.True(delivered, "Timed out waiting for the operator's own node to receive the push.");
            var otherDelivered = await WaitUntilAsync(() => otherNodeDispatcher.Dispatches.Count > 0, TimeSpan.FromSeconds(20));
            Assert.True(otherDelivered, "Timed out waiting for the second connection's own node to receive the push.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumer.StopAsync(CancellationToken.None);
            await otherNodeConsumer.StopAsync(CancellationToken.None);
        }

        var received = Assert.Single(localDispatcher.Dispatches);
        Assert.Equal(operatorConnection, received.ConnectionId);
        Assert.Equal("PendingBookingsChanged", received.Method);

        // camelCase on the wire, WireJsonOptions's own whole point (`5-11`'s bug, restated for this
        // product) - a plain PascalCase deserialize here would silently read every field as default.
        var payload = JsonSerializer.Deserialize<JsonElement>(received.PayloadJson);
        Assert.Equal(booking.Id.Value, payload.GetProperty("eventId").GetGuid());
        Assert.Equal(seed.Tenant.Id.Value, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal("Cancelled", payload.GetProperty("status").GetString());

        Assert.Single(otherNodeDispatcher.Dispatches);
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        // Not IAsyncDisposable-registered here: RabbitMqEventPublisher.DisposeAsync only disposes its
        // own channel, never the RabbitMqConnection it was given - the test method's own
        // `await using` on fanoutPublisherConnection owns that lifetime, the identical shape
        // ConnectionFanoutEndToEndTests' own BuildServiceProvider uses.
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveBookingPendingDeliveryTargetsHandler>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// This item's own minimal seed - <c>CalendarSeed.WriteAsync</c> is not reusable here because it
    /// is typed to <c>PostgresFixture</c>, not this suite's own <see cref="BookingPendingFanoutFixture"/>
    /// (the identical constraint <c>ModuleQuantityImpactRequestedWireTests.SeedTenantWithTwoActiveWorkersAsync</c>
    /// already worked around for its own, differently-typed fixture pairing). Skips the real
    /// <c>RoleAssignmentProjectionStore</c> row <c>CalendarSeed</c> writes: this suite proves the
    /// fan-out, not authorization, so <see cref="AllowingPermissionChecker"/> stands in for
    /// <c>PermissionChecker</c> and there is no projection row for it to need.
    /// </summary>
    private async Task<SeededTenant> SeedTenantAsync()
    {
        var tenant = Tenant.Register(
            new TenantId(CalendarSeed.NewId()), "Fanout Test Shop", new TenantPublicKey($"fanout-{CalendarSeed.NewId():N}"), Now);
        var calendar = BookingCalendar.Create(new CalendarId(CalendarSeed.NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), Now);
        var worker = Domain.Worker.Create(new WorkerId(CalendarSeed.NewId()), tenant.Id, "Doe", "Alex", null, Now);
        var service = Service.Create(new ServiceId(CalendarSeed.NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45), price: null);

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        await db.SaveChangesAsync();

        return new SeededTenant(
            tenant, calendar, worker, service, Customer: null!, OperatorId: default, ExternalSubjectId: "");
    }

    private async Task<Event> APendingBookingAsync(SeededTenant seed)
    {
        var slot = CalendarSeed.Slot(seed, Now.AddDays(3));

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);

        var customer = Customer.Register(new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, new PhoneNumber("+79996000001"), Now);
        await new CustomerRepository(db).AddAsync(customer, CancellationToken.None);

        // Constructed through the aggregate rather than through BookingStore, the identical shape
        // ConfirmationSweepTests' own APendingBookingAsync uses and for the identical reason: what
        // this test needs is a row that is genuinely PendingConfirmation, not a proof of the claim
        // path itself (BookingStoreTests already covers the claim's own half of this item's push).
        slot.Claim(customer.Id, seed.Service.Id, Now, Now.AddMinutes(15));
        slot.ClearDomainEvents();
        await new EventRepository(db).SaveAsync(slot, CancellationToken.None);

        return slot;
    }

    private static async Task AwaitCompetingSubscriptionsAsync(
        HttpClient management, TimeSpan timeout, params (string Topic, string ConsumerName)[] subscriptions)
    {
        foreach (var (topic, consumerName) in subscriptions)
        {
            var queueName = $"{topic}.{consumerName}";
            var landed = await WaitUntilAsync(
                async () => await GetQueueConsumerCountAsync(management, queueName) is >= 1, timeout);
            Assert.True(landed, $"'{queueName}' never attached a live consumer.");
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }

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

    private sealed class FakeLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public ConcurrentBag<(ConnectionId ConnectionId, string Method, string PayloadJson)> Dispatches { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.FromResult(DispatchOutcome.Delivered);
        }
    }

    private sealed class AllowingPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(
            OperatorId operatorId, TenantId tenantId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
