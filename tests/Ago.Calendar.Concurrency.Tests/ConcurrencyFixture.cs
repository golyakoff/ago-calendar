using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>One real Postgres for this assembly, with this product's migrations applied from
/// scratch. Separate from <c>Ago.Calendar.Integration.Tests</c>' fixture because the assemblies are
/// separate; the machine-wide Docker lock keeps the two containers from being alive at once.</summary>
public sealed class ConcurrencyFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();

        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
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

internal sealed record SeededCalendar(TenantId TenantId, CalendarId CalendarId, WorkerId WorkerId);
