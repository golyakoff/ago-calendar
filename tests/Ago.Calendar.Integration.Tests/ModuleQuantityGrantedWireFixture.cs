using System.Net.Http.Headers;
using System.Text;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-66`: one real Postgres and one real RabbitMQ per collection - the identical "never mock the
/// broker either" posture <c>Ago.Chat.Integration.Tests.OutboxDispatcherFixture</c> already
/// established on the publishing side, mirrored here for the receiving one. Kept apart from the
/// shared <see cref="PostgresFixture"/>/<see cref="PostgresCollection"/> every other suite in this
/// project uses (which carries no broker) rather than widening that fixture - a RabbitMQ container
/// every Postgres-collection test would pay for and almost none of them need.
/// </summary>
public sealed class ModuleQuantityGrantedWireFixture : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";

    // `Ago.Chat.Integration.Tests.ConnectionFanoutFixture`'s own remarks: not exposed by
    // `RabbitMqBuilder` by default (only 5672 is), even though `rabbitmq:4-management`'s own image
    // always runs the plugin - explicit `WithPortBinding` is what actually publishes it to the host.
    // Needed here because plain AMQP has no way to ask "is a consumer actually attached to this queue
    // yet," and publishing before the real, first-ever `Competing` queue exists loses the message
    // rather than queuing it (the identical `0/6` race that item's own remarks describe).
    private const int ManagementPort = 15672;

    private PostgreSqlContainer _postgres = null!;
    private IDisposable _dockerLock = null!;

    public RabbitMqContainer RabbitMq { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        RabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(Username).WithPassword(Password)
            .WithPortBinding(ManagementPort, true)
            .Build();
        await Task.WhenAll(_postgres.StartAsync(), RabbitMq.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _postgres.DisposeAsync();
        await RabbitMq.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(DataSource).Options;
        return new AgoCalendarDbContext(options);
    }

    /// <summary>A client against this container's own RabbitMQ management API - basic auth, the same
    /// credentials used over AMQP; the management plugin accepts the same user/password pair over
    /// HTTP (the identical client shape <c>Ago.Chat.Integration.Tests.ConnectionFanoutFixture</c>'s
    /// own remarks establish).</summary>
    public HttpClient CreateRabbitMqManagementClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{RabbitMq.Hostname}:{RabbitMq.GetMappedPublicPort(ManagementPort)}"),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ModuleQuantityGrantedWireCollection : ICollectionFixture<ModuleQuantityGrantedWireFixture>
{
    public const string Name = "ModuleQuantityGrantedWire";
}
