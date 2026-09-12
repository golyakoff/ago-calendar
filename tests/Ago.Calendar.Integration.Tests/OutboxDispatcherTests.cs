using System.Collections.Concurrent;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `25-44`'s own fails-before demonstration: `Ago.Calendar.Worker` had no <see cref="OutboxDispatcher"/>
/// at all before this item, so nothing published a staged row to the broker no matter how correctly it
/// was written. Real Postgres, real RabbitMQ, no mocking either (testing.md) - reuses
/// <see cref="ModuleQuantityGrantedWireFixture"/> rather than a second container pair, the same
/// "nothing here needs a different fixture" reasoning <see cref="ModuleQuantityImpactRequestedWireTests"/>
/// already gives for itself.
///
/// <para><b>Both real rows this product stages, not only the newest one.</b> `25-44`'s own scope
/// warning: `BookingConfirmed` has had this identical gap since `20-04`, and a fix scoped to only
/// `ModuleQuantityImpactComputed` would leave it sitting right next to the one just closed. Both
/// staged through their real, unmodified production handlers -
/// <see cref="ExpiredBookingConfirmer"/>/<see cref="WorkerQuotaImpactAnswerer"/> - never a
/// hand-built envelope standing in for either.</para>
/// </summary>
[Collection(ModuleQuantityGrantedWireCollection.Name)]
public sealed class OutboxDispatcherTests(ModuleQuantityGrantedWireFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ABookingConfirmedRow_AndAModuleQuantityImpactComputedRow_BothStagedByTheirRealHandlers_AreActuallyDelivered()
    {
        var seed = await WriteTenantAsync();
        var booking = await APendingBookingAsync(seed);

        // `20-04`'s own real sweep - the unmodified ExpiredBookingConfirmer, the same class a real
        // Ago.Calendar.Worker tick calls, staging BookingConfirmed on the real outbox in the same
        // transaction as the PendingConfirmation -> Booked transition (CLAUDE.md rule 4).
        await using (var db = fixture.CreateDbContext())
        {
            var confirmer = new ExpiredBookingConfirmer(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator());
            var confirmedCount = await confirmer.ConfirmExpiredAsync(seed.Tenant.Id, booking.ConfirmationDeadline!.Value, batchSize: 10, CancellationToken.None);
            Assert.Equal(1, confirmedCount);
        }

        // `23-88`/`adr/0165`'s own real answerer - the unmodified WorkerQuotaImpactAnswerer, the same
        // class ModuleQuantityImpactRequestedConsumer calls per message, staging
        // ModuleQuantityImpactComputed on the same real outbox.
        var correlationId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var answerer = new WorkerQuotaImpactAnswerer(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator());
            await answerer.AnswerAsync(seed.Tenant.Id, requestedQuantity: 1, correlationId, Now, CancellationToken.None);
        }

        await using (var verify = fixture.CreateDbContext())
        {
            var staged = await verify.Set<OutboxMessage>().CountAsync(o => o.PublishedAt == null, CancellationToken.None);
            // `25-63`: three, not two, since ExpiredBookingConfirmer now also stages
            // BookingPendingStateChanged ("Booked", the sweep's own half of that item's push)
            // alongside BookingConfirmed, in the identical transaction - all three committed, none
            // published yet, the exact "sits there forever" this item fixes.
            Assert.Equal(3, staged);
        }

        var receivedBookingConfirmed = new ConcurrentBag<EventEnvelope>();
        var receivedImpactComputed = new ConcurrentBag<EventEnvelope>();

        await using var bookingConsumerConnection = CreateRabbitMqConnection();
        var bookingConsumer = new RabbitMqEventConsumer(bookingConsumerConnection);
        await bookingConsumer.SubscribeAsync(
            nameof(BookingConfirmed), SubscriptionMode.Broadcast, "test-consumer-booking-confirmed",
            new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { receivedBookingConfirmed.Add(envelope); return ctx.AckAsync(ct); }, CancellationToken.None);

        await using var impactConsumerConnection = CreateRabbitMqConnection();
        var impactConsumer = new RabbitMqEventConsumer(impactConsumerConnection);
        await impactConsumer.SubscribeAsync(
            nameof(ModuleQuantityImpactComputed), SubscriptionMode.Broadcast, "test-consumer-impact-computed",
            new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { receivedImpactComputed.Add(envelope); return ctx.AckAsync(ct); }, CancellationToken.None);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => !receivedBookingConfirmed.IsEmpty && !receivedImpactComputed.IsEmpty, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        var bookingEnvelope = Assert.Single(receivedBookingConfirmed);
        var bookingContract = JsonSerializer.Deserialize<BookingConfirmed>(bookingEnvelope.Payload)!;
        Assert.Equal(booking.Id.Value, bookingContract.EventId);
        Assert.Equal(seed.Tenant.Id.Value, bookingContract.TenantId);

        var impactEnvelope = Assert.Single(receivedImpactComputed);
        var impactContract = JsonSerializer.Deserialize<ModuleQuantityImpactComputed>(impactEnvelope.Payload)!;
        Assert.Equal(seed.Tenant.Id.Value, impactContract.SiteId);
        Assert.Equal(correlationId, impactContract.CorrelationId);

        // Not merely received - actually marked published_at, the same double proof
        // Ago.Chat.Integration.Tests.OutboxDispatcherTests.RowWrittenByTheRealHandlerPath_IsPublishedAndMarked
        // insists on: a broker-side receipt with no Postgres-side mark would still be a dispatcher
        // that could re-publish the identical row forever.
        await using var verifyPublished = fixture.CreateDbContext();
        var stillUnpublished = await verifyPublished.Set<OutboxMessage>().CountAsync(o => o.PublishedAt == null, CancellationToken.None);
        Assert.Equal(0, stillUnpublished);
    }

    /// <summary>The identical safety property `Ago.Chat.Worker.OutboxDispatcher`'s own
    /// `TwoDispatchers_RacingForTheSameBatch_EachRowPublishedExactlyOnce` proves - this item is a port
    /// of that class, and the concurrency guarantee (`FOR UPDATE SKIP LOCKED`, adr/0005) it depends on
    /// is exactly what a second `Ago.Calendar.Worker` replica in production would lean on. Proven
    /// again here rather than assumed to carry over unchanged, since it is this product's own database
    /// and its own two dispatcher instances doing the racing, not a fact inherited from chat's test
    /// suite.</summary>
    [Fact]
    public async Task TwoDispatchers_RacingForTheSameBatch_EachRowPublishedExactlyOnce()
    {
        const int rowCount = 20;
        var ids = await SeedOutboxRowsAsync(rowCount);

        await using var consumerConnection = CreateRabbitMqConnection();
        var consumer = new RabbitMqEventConsumer(consumerConnection);
        var receivedIds = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(
            "TestTopic", SubscriptionMode.Broadcast, "test-consumer-race",
            new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { receivedIds.Add(envelope.MessageId); return ctx.AckAsync(ct); }, CancellationToken.None);

        var dispatcherA = CreateDispatcher();
        var dispatcherB = CreateDispatcher();
        await dispatcherA.StartAsync(CancellationToken.None);
        await dispatcherB.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => await AllPublishedAsync(ids), TimeSpan.FromSeconds(20));
        }
        finally
        {
            await dispatcherA.StopAsync(CancellationToken.None);
            await dispatcherB.StopAsync(CancellationToken.None);
        }

        await WaitUntilAsync(() => receivedIds.Count >= rowCount, TimeSpan.FromSeconds(5));

        Assert.True(await AllPublishedAsync(ids));
        Assert.Equal(rowCount, receivedIds.Count);
        Assert.Equal(rowCount, receivedIds.Distinct().Count());
    }

    private OutboxDispatcher CreateDispatcher(int batchSize = 20)
    {
        var connection = CreateRabbitMqConnection();
        return new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2), BatchSize = batchSize }),
            NullLogger<OutboxDispatcher>.Instance);
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

    private async Task<IReadOnlyList<Guid>> SeedOutboxRowsAsync(int count)
    {
        await using var db = fixture.CreateDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Add(new OutboxMessage(id, DateTimeOffset.UtcNow, "TestTopic", 1, "{}", $"key-{id:N}", Guid.NewGuid()));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return ids;
    }

    private async Task<bool> AllPublishedAsync(IReadOnlyList<Guid> ids)
    {
        await using var db = fixture.CreateDbContext();
        var publishedCount = await db.Set<OutboxMessage>().CountAsync(o => ids.Contains(o.Id) && o.PublishedAt != null, CancellationToken.None);
        return publishedCount == ids.Count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Condition not met within {timeout}.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Condition not met within {timeout}.");
    }

    // ------------------------------------------------------------------------------------------
    // Minimal tenant/booking seeding - CalendarSeed.WriteAsync's own logic, restated against this
    // file's own ModuleQuantityGrantedWireFixture rather than called directly: that helper is typed
    // to the plain PostgresFixture/PostgresCollection every other suite in this project shares, which
    // carries no broker - widening its signature for the one test file that also needs RabbitMQ
    // would be a shared-helper change with no other caller, so this file grows its own minimal
    // version instead (CalendarSeed.Slot/NewId/AllPermissions, which are fixture-independent, are
    // still reused directly).
    // ------------------------------------------------------------------------------------------

    private async Task<SeededTenant> WriteTenantAsync()
    {
        var tenant = Tenant.Register(
            new TenantId(CalendarSeed.NewId()), "Barbershop",
            new TenantPublicKey($"shop-{CalendarSeed.NewId():N}"[..24]), Now, allowedOrigins: null);
        var calendar = BookingCalendar.Create(new CalendarId(CalendarSeed.NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), Now);
        var worker = Domain.Worker.Create(new WorkerId(CalendarSeed.NewId()), tenant.Id, "Doe", "Alex", null, Now);
        var service = Service.Create(new ServiceId(CalendarSeed.NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45), price: null);
        var customer = Customer.Register(new CustomerId(CalendarSeed.NewId()), tenant.Id, new PhoneNumber("+79997000099"), Now);

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        return new SeededTenant(tenant, calendar, worker, service, customer, OperatorId.FromExternalSubjectId("kc-unused"), "kc-unused");
    }

    /// <summary>The identical shape `ConfirmationSweepTests.APendingBookingAsync` already
    /// establishes for the same claim - a `PendingConfirmation` row with a known deadline, constructed
    /// through the aggregate rather than through `BookingStore`, since what this test needs is a real
    /// row for the real sweep to confirm, not a proof of the claim path itself.</summary>
    private async Task<Event> APendingBookingAsync(SeededTenant seed)
    {
        var deadline = Now.AddMinutes(15);
        var slot = CalendarSeed.Slot(seed, Now.AddDays(3));

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);

        slot.Claim(seed.Customer.Id, seed.Service.Id, Now, deadline);
        slot.ClearDomainEvents();
        await new EventRepository(db).SaveAsync(slot, CancellationToken.None);

        return slot;
    }
}
