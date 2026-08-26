using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>One real Postgres for this assembly, with this product's migrations applied from
/// scratch. Separate from <c>Ago.Calendar.Integration.Tests</c>' fixture because the assemblies are
/// separate; the machine-wide Docker lock keeps the two containers from being alive at once.</summary>
public sealed class ConcurrencyFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private RedisContainer _redis = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>`20-03`: a real Redis for the booking endpoint's rate-limit buckets. The token
    /// bucket is a Lua script executing inside Redis, so a fake of it would prove only that the fake
    /// behaves as its author expected - `3-05`'s own RateLimitingConcurrencyTests set that bar.</summary>
    public IConnectionMultiplexer RedisMultiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _redis = new RedisBuilder("redis:7-alpine").Build();
        await Task.WhenAll(_container.StartAsync(), _redis.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        RedisMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await RedisMultiplexer.DisposeAsync();
        await DataSource.DisposeAsync();
        await _redis.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    /// <summary>A context on its own pooled connection - what two genuinely simultaneous writers
    /// need, since a shared <c>DbContext</c> is not thread-safe and would serialise the very race
    /// under test.</summary>
    public AgoCalendarDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(DataSource).Options;
        return new AgoCalendarDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class ConcurrencyCollection : ICollectionFixture<ConcurrencyFixture>
{
    public const string Name = "Concurrency";
}

/// <summary>A clock the test owns - adr/0011.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}

internal sealed record SeededCalendar(TenantId TenantId, CalendarId CalendarId, WorkerId WorkerId)
{
    /// <summary>`20-03`: the service a booking names. Init-only with a default so `20-02`'s own
    /// materialisation tests, which have no service to name, construct this unchanged.</summary>
    public ServiceId ServiceId { get; init; }
}
