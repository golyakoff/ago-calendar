using System.Net.Http.Headers;
using System.Text;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `25-63`: the same three-container combination `Ago.Chat.Integration.Tests.ConnectionFanoutFixture`
/// pays for - Postgres (the booking and its outbox row), RabbitMQ
/// (<c>BookingPendingStateChanged</c> and its per-node delivery), Redis (the connection registry) -
/// kept apart from <see cref="ModuleQuantityGrantedWireFixture"/> (Postgres+RabbitMQ, no Redis) for
/// the identical reason that fixture's own remarks give: existing tests on it do not need Redis and
/// should not pay for starting it.
/// </summary>
public sealed class BookingPendingFanoutFixture : IAsyncLifetime
{
    private const string RabbitMqUsername = "ago-test";
    private const string RabbitMqPassword = "ago-test-local-dev";

    // `15-17`: the RabbitMQ management API port - not exposed by RabbitMqBuilder by default (only
    // 5672 is). Every subscription wait in this fixture's own tests goes through it, since plain AMQP
    // has no way to ask "is a consumer actually attached to this queue yet" - the identical reasoning
    // ModuleQuantityGrantedWireFixture's own remarks give.
    private const int RabbitMqManagementPort = 15672;

    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private RedisContainer _redis = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IConnectionMultiplexer RedisMultiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(RabbitMqUsername).WithPassword(RabbitMqPassword)
            .WithPortBinding(RabbitMqManagementPort, true)
            .Build();
        _redis = new RedisBuilder("redis:7-alpine").Build();

        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync(), _redis.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        RedisMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await RedisMultiplexer.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(DataSource).Options;
        return new AgoCalendarDbContext(options);
    }

    public RabbitMqConnection CreateRabbitMqConnection() => new(Options.Create(new RabbitMqOptions
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqUsername,
        Password = RabbitMqPassword,
    }), NullLogger<RabbitMqConnection>.Instance);

    /// <summary>A client against this container's own RabbitMQ management API - basic auth, the same
    /// credentials <see cref="CreateRabbitMqConnection"/> already uses over AMQP.</summary>
    public HttpClient CreateRabbitMqManagementClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbitMq.Hostname}:{_rabbitMq.GetMappedPublicPort(RabbitMqManagementPort)}"),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RabbitMqUsername}:{RabbitMqPassword}")));
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class BookingPendingFanoutCollection : ICollectionFixture<BookingPendingFanoutFixture>
{
    public const string Name = "BookingPendingFanout";
}
