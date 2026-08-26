using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// One real Postgres per collection, this product's migrations applied once from scratch - which is
/// itself the first thing this suite proves (testing.md, and `20-01`'s own done-when). Tests isolate
/// themselves with fresh tenant ids rather than by truncating, so nothing depends on execution
/// order.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private RedisContainer _redis = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>`20-03`: what a host is configured with. The booking endpoint's own test runs the
    /// real <c>Ago.Calendar.Api</c> in-process, and <c>AddCalendarPostgresPersistence</c> takes a
    /// connection string, not a data source.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>`20-03`: a real Redis, because the booking endpoint's rate limiter is a real one.
    /// Added to this fixture rather than to a third one so the suite pays for one extra container,
    /// not one per collection.</summary>
    public string RedisConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _redis = new RedisBuilder("redis:7-alpine").Build();
        await Task.WhenAll(_container.StartAsync(), _redis.StartAsync());

        ConnectionString = _container.GetConnectionString();
        RedisConnectionString = _redis.GetConnectionString();
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _redis.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(DataSource).Options;
        return new AgoCalendarDbContext(options);
    }

    /// <summary>A context bound to one caller-owned connection and transaction - what the overlap
    /// race needs, because two writers have to be genuinely open at the same time rather than
    /// sequential awaits pretending to be concurrent.</summary>
    public AgoCalendarDbContext CreateDbContext(NpgsqlConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(connection).Options;
        return new AgoCalendarDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
