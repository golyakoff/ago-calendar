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
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-08`/`adr/0149` rule 1: `docs/backlog/22-08-*.md`'s own broker-stopped Done-when, made good
/// on - "with the broker stopped, a suspended tenant's calendar refuses a new booking... proven by
/// stopping the broker, not by reading the code. This is the box that makes the bound a bound."
///
/// <para><b>Deliberately its own file, its own containers, no shared fixture</b> -
/// <see cref="TenantSuspensionChangedWireTests"/>'s own class remarks state why: this class implements
/// <see cref="IAsyncLifetime"/> directly rather than through <see cref="ICollectionFixture{TFixture}"/>,
/// so xUnit gives each `[Fact]` its own fresh instance and therefore its own fresh Postgres and RabbitMQ
/// pair - a test that genuinely stops its own RabbitMQ container cannot take any other test down with
/// it, because there is no other test sharing that container.</para>
///
/// <para><b>What each test proves.</b>
/// <see cref="ARecentlyEstablishedLease_KeepsRefusingBookings_EvenAfterTheBrokerIsStopped"/>: a lease
/// is established over a real, live broker; the broker container is then genuinely torn down; a real
/// <see cref="BookingStore"/> claim against real Postgres - the exact statement production runs - is
/// shown to still refuse. The enforcement rests on the already-durable row and the local clock, never
/// on the broker staying up.
/// <see cref="OnceTheLeaseFullyLapsesWithNoRenewal_ABookingIsNoLongerRefused"/>: the named, accepted
/// far edge of the identical mechanism - `adr/0166`'s own stated cost, "continued violation for up to
/// L if the broker is down" - demonstrated, not merely asserted.</para>
/// </summary>
public sealed class TenantSuspensionBrokerStoppedTests : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
    private const int ManagementPort = 15672;
    private const string ConsumerName = "calendar-worker-suspension-lease";
    private const string Topic = "TenantSuspensionChanged";

    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private IDisposable _dockerLock = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(Username).WithPassword(Password)
            .WithPortBinding(ManagementPort, true)
            .Build();
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        _dataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
        // Already stopped by the test itself in both cases below - DisposeAsync on an
        // already-stopped Testcontainers resource is a no-op, not a throw.
        await _rabbitMq.DisposeAsync();
        _dockerLock.Dispose();
    }

    [Fact]
    public async Task ARecentlyEstablishedLease_KeepsRefusingBookings_EvenAfterTheBrokerIsStopped()
    {
        var seed = await SeedTenantWithAvailableSlotAsync();
        // `22-08` verification, 2026-09-13: this used to be `AddSeconds(6)`, computed before the
        // consumer-subscription wait, the publish, and the propagation wait all happen - under real
        // load (a full-suite run, not this file in isolation) that overhead alone was observed to eat
        // the entire 6-second budget, so the assertion below raced the lease's own expiry and failed
        // once out of several runs. 20 seconds gives the same real chain of real waits (subscription
        // confirmation, publish, propagation, broker teardown) comfortable headroom without weakening
        // what the test proves - the claim below still runs against a real, unexpired lease row.
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(20);

        await RunConsumerAsync(async () =>
        {
            await PublishSuspensionAsync(seed.TenantId, leaseUntil);
            await WaitUntilAsync(
                async () => IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil),
                TimeSpan.FromSeconds(20));
        });

        // The broker is genuinely stopped here - not paused, not disconnected, the Testcontainers
        // container itself is torn down. No further message of any kind can cross the wire from this
        // point on; anything ago-chat might have tried to publish next (a renewal, an immediate lift)
        // is now unreachable, exactly the scenario `adr/0149`'s own "broker stopped, consumer crashed,
        // chat itself down" names.
        await _rabbitMq.StopAsync();

        // Still inside the lease's own window (leaseUntil is a few seconds out) - the claim refuses,
        // proven against real Postgres with the broker already down. The already-durable
        // suspension_valid_until row and the local clock are all this decision rests on.
        var confirmation = await BookAsync(seed, DateTimeOffset.UtcNow);

        Assert.Null(confirmation);
    }

    [Fact]
    public async Task OnceTheLeaseFullyLapsesWithNoRenewal_ABookingIsNoLongerRefused()
    {
        var seed = await SeedTenantWithAvailableSlotAsync();
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(2);

        await RunConsumerAsync(async () =>
        {
            await PublishSuspensionAsync(seed.TenantId, leaseUntil);
            await WaitUntilAsync(
                async () => IsCloseTo(await GetSuspensionValidUntilAsync(seed.TenantId), leaseUntil),
                TimeSpan.FromSeconds(20));
        });

        await _rabbitMq.StopAsync();

        // Real wall-clock wait past the lease's own instant - no further renewal can arrive, the
        // broker is down, and nothing here fast-forwards a clock: this is the actual passage of time
        // the ceiling is stated in terms of.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var confirmation = await BookAsync(seed, DateTimeOffset.UtcNow);

        Assert.NotNull(confirmation);
    }

    // ------------------------------------------------------------------------------------------
    // Wire plumbing - deliberately duplicated from TenantSuspensionChangedWireTests rather than
    // shared: this class's own containers are private fields, not a fixture object those helpers
    // could take as a parameter without either a shared base class or an interface neither file
    // otherwise needs.
    // ------------------------------------------------------------------------------------------

    private AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(_dataSource).Options;
        return new AgoCalendarDbContext(options);
    }

    private HttpClient CreateRabbitMqManagementClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbitMq.Hostname}:{_rabbitMq.GetMappedPublicPort(ManagementPort)}"),
        };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Username}:{Password}")));
        return client;
    }

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
        services.AddSingleton(_dataSource);
        services.AddDbContext<AgoCalendarDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
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
            using var management = CreateRabbitMqManagementClient();
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
            HostName = _rabbitMq.Hostname,
            Port = _rabbitMq.GetMappedPublicPort(5672),
            UserName = Username,
            Password = Password,
        }),
        NullLogger<RabbitMqConnection>.Instance);

    private async Task<DateTimeOffset?> GetSuspensionValidUntilAsync(TenantId tenantId)
    {
        await using var db = CreateDbContext();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, CancellationToken.None);
        return tenant.SuspensionValidUntil;
    }

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

    private sealed record SeededTenantWithSlot(TenantId TenantId, CalendarId CalendarId, EventId SlotId);

    private async Task<SeededTenantWithSlot> SeedTenantWithAvailableSlotAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Register(
            new TenantId(NewId(now)), "Suspension Broker Test Shop", new TenantPublicKey($"brk-{NewId(now):N}"[..24]), now);
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

        await using var db = CreateDbContext();
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
        await using var db = CreateDbContext();
        return await new BookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator()).TryBookAsync(
            new BookingAttempt(
                seed.TenantId,
                seed.CalendarId,
                [seed.SlotId],
                (await db.Services.AsNoTracking().Select(s => s.Id).FirstAsync()),
                new PhoneNumber($"+7999{Random.Shared.Next(1000000, 9999999)}"),
                new PersonRegistration("Broker Test Customer"),
                now,
                now.AddMinutes(15),
                now,
                NewId(now),
                null),
            CancellationToken.None);
    }

    private static Guid NewId(DateTimeOffset now) => Guid.CreateVersion7(now);
}
