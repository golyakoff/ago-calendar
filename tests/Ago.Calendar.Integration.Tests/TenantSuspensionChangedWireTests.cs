using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
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
/// `22-08`/`adr/0149` rule 1: the wire proof for the account-wide suspension lease, the identical
/// "real broker, real consumer, hand-built envelope standing in for `ago-chat`'s own process" shape
/// <see cref="ModuleQuantityGrantedWireTests"/> already establishes for the quota crossing - see that
/// class's own remarks for what is real and what stands in, restated here for a second contract riding
/// the identical mechanism. This file's own two tests never touch the broker's own lifecycle - see
/// <c>TenantSuspensionBrokerStoppedTests</c> (this same test project) for the broker-stopped
/// Done-when, deliberately kept in its own file with its own, non-shared containers: a test that
/// actually stops <see cref="TenantSuspensionChangedWireFixture.RabbitMq"/> would otherwise take this
/// class's shared collection fixture down with it for every other test sharing it.
/// </summary>
[Collection(TenantSuspensionChangedWireCollection.Name)]
public sealed class TenantSuspensionChangedWireTests(TenantSuspensionChangedWireFixture fixture)
{
    private const string ConsumerName = "calendar-worker-suspension-lease";
    private const string Topic = "TenantSuspensionChanged";

    [Fact]
    public async Task ALeasePublishedOverTheRealBroker_IsAppliedByTheRealConsumer()
    {
        var seed = await SeedTenantWithAvailableSlotAsync();
        var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        await RunConsumerAsync(async () =>
        {
            // Timed from here, deliberately - not from before RunConsumerAsync's own subscription
            // wait (up to 15s of test-harness startup, never part of production propagation). This
            // is the ordinary-case number `docs/backlog/22-08-*.md`'s own Done-when asks the report
            // to carry: one outbox hop, over the real broker, to a real, already-subscribed
            // consumer, into real Postgres.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await PublishSuspensionAsync(seed.TenantId, leaseUntil);
            await WaitUntilAsync(
                async () => IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil), TimeSpan.FromSeconds(20));
            stopwatch.Stop();

            // Recorded to a file the worker's own report reads back, rather than a number typed from
            // memory - `docs/backlog/22-08-*.md`'s own "with the observed elapsed time in the
            // report."
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "22-08-propagation-ms.txt"),
                stopwatch.ElapsedMilliseconds.ToString());
        });

        Assert.True(IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil));

        var confirmation = await BookAsync(seed, DateTimeOffset.UtcNow);
        Assert.Null(confirmation);
    }

    /// <summary>The redelivery half - the identical "at-least-once means the real consumer must see
    /// the fact twice and land on the same state" property `ModuleQuantityGrantedWireTests`' own
    /// sibling test proves for the quota.</summary>
    [Fact]
    public async Task TheIdenticalLease_PublishedTwiceOverTheRealBroker_IsANoOpTheSecondTime()
    {
        var seed = await SeedTenantWithAvailableSlotAsync();
        var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        await RunConsumerAsync(async () =>
        {
            await PublishSuspensionAsync(seed.TenantId, leaseUntil);
            await PublishSuspensionAsync(seed.TenantId, leaseUntil);
            await WaitUntilAsync(
                async () => IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil), TimeSpan.FromSeconds(20));
            await Task.Delay(TimeSpan.FromSeconds(1));
        });

        Assert.True(IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil));
    }

    // ------------------------------------------------------------------------------------------
    // Wire plumbing - real classes throughout, no fakes. Mirrors ModuleQuantityGrantedWireTests'
    // own plumbing section; not shared, the identical "each wire-test file owns its own topic name
    // and consumer name, no symbol across the assembly boundary to derive them from" reasoning that
    // file's own remarks (and RabbitMqSubscriptionTestHelpers') already state.
    // ------------------------------------------------------------------------------------------

    private async Task PublishSuspensionAsync(TenantId tenantId, DateTimeOffset? suspendedUntil)
    {
        await using var connection = CreateRabbitMqConnection();
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);

        var correlationId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            SiteId = tenantId.Value,
            SuspendedUntil = suspendedUntil,
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
        });

        await publisher.PublishAsync(
            new EventEnvelope(
                MessageId: Guid.NewGuid(),
                Type: Topic,
                Version: 1,
                PartitionKey: tenantId.Value.ToString(),
                OccurredAt: occurredAt,
                CorrelationId: correlationId,
                Payload: payload),
            CancellationToken.None);
    }

    private async Task RunConsumerAsync(Func<Task> afterSubscribed)
    {
        await using var connection = CreateRabbitMqConnection();
        var consumer = new RabbitMqEventConsumer(connection);

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddDbContext<AgoCalendarDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UtcNow));
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ISuspensionLeaseStore, SuspensionLeaseStore>();
        services.AddScoped<IInboxChecker, EfInboxChecker<AgoCalendarDbContext>>();
        await using var provider = services.BuildServiceProvider();

        var backgroundConsumer = new TenantSuspensionChangedConsumer(
            consumer,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TenantSuspensionChangedConsumerOptions()),
            NullLogger<TenantSuspensionChangedConsumer>.Instance);

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

    private async Task<DateTimeOffset?> GetSuspensionValidUntilAsync(TenantId tenantId)
    {
        await using var db = fixture.CreateDbContext();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, CancellationToken.None);
        return tenant.SuspensionValidUntil;
    }

    /// <summary>Within a millisecond - Postgres `timestamptz` stores microsecond precision, one digit
    /// short of a .NET <see cref="DateTimeOffset"/> tick's own 100ns resolution, so a bit-exact
    /// equality after a real round trip through the database is not a guarantee this test should rely
    /// on. A millisecond is far coarser than that truncation and still tight enough to catch a
    /// genuinely wrong value.</summary>
    private static bool IsCloseTo(DateTimeOffset? actual, DateTimeOffset expected) =>
        actual is { } value && Math.Abs((value - expected).TotalMilliseconds) < 1;

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
    // Seeding and booking - a real slot in the near future (real wall-clock `DateTimeOffset.UtcNow`,
    // not a fixed historical instant like `BookingStoreTests`' own `Now`): this file's own tests
    // compare a real, currently-advancing clock against a lease instant, so the slot and every claim
    // attempt have to be anchored to the same real clock.
    // ------------------------------------------------------------------------------------------

    private sealed record SeededTenantWithSlot(TenantId TenantId, CalendarId CalendarId, EventId SlotId);

    private async Task<SeededTenantWithSlot> SeedTenantWithAvailableSlotAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Register(
            new TenantId(NewId(now)), "Suspension Wire Test Shop", new TenantPublicKey($"susp-{NewId(now):N}"[..24]), now);
        var calendar = BookingCalendar.Create(new CalendarId(NewId(now)), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), now);
        calendar.Publish();
        var worker = Domain.Worker.Create(new WorkerId(NewId(now)), tenant.Id, "Doe", "Alex", null, now);
        var service = Service.Create(new ServiceId(NewId(now)), tenant.Id, "Haircut", TimeSpan.FromMinutes(45), null);
        var startsAt = now.AddHours(2);
        var slot = Event.Materialize(
            new EventId(NewId(now)), tenant.Id, calendar.Id, worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)),
            DateOnly.FromDateTime(startsAt.UtcDateTime),
            now);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Workers.Add(worker);
        db.Services.Add(service);
        db.Events.Add(slot);
        await db.SaveChangesAsync();

        return new SeededTenantWithSlot(tenant.Id, calendar.Id, slot.Id);
    }

    private async Task<BookingConfirmation?> BookAsync(SeededTenantWithSlot seed, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        return await new BookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator()).TryBookAsync(
            new BookingAttempt(
                seed.TenantId,
                seed.CalendarId,
                [seed.SlotId],
                (await db.Services.AsNoTracking().Select(s => s.Id).FirstAsync()),
                new PhoneNumber($"+7999{Random.Shared.Next(1000000, 9999999)}"),
                new PersonRegistration("Wire Test Customer"),
                now,
                now.AddMinutes(15),
                now,
                NewId(now),
                null),
            CancellationToken.None);
    }

    private static Guid NewId(DateTimeOffset now) => Guid.CreateVersion7(now);
}
