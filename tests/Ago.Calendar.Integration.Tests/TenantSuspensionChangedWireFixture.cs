using System.Net.Http.Headers;
using System.Text;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-08`: one real Postgres and one real, independently stoppable RabbitMQ per collection - the
/// identical shape <see cref="ModuleQuantityGrantedWireFixture"/> already establishes for its own
/// wire, not widened to share it: this suite's own broker-stopped Done-when
/// (`docs/backlog/22-08-*.md`: "with the broker stopped, a suspended tenant's calendar refuses a new
/// booking... proven by stopping the broker, not by reading the code") needs to call
/// <c>RabbitMq.StopAsync()</c> mid-test, which no other suite in this project does and which a shared
/// fixture would make every other broker-using test pay for accidentally inheriting.
/// </summary>
public sealed class TenantSuspensionChangedWireFixture : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
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
        // `22-08`'s own test may already have stopped this container - StopAsync/DisposeAsync on an
        // already-stopped Testcontainers resource is a no-op, not a throw.
        await RabbitMq.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(DataSource).Options;
        return new AgoCalendarDbContext(options);
    }

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
public sealed class TenantSuspensionChangedWireCollection : ICollectionFixture<TenantSuspensionChangedWireFixture>
{
    public const string Name = "TenantSuspensionChangedWire";
}
