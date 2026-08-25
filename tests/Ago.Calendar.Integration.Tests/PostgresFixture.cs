using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

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
