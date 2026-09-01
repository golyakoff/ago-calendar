using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-13`'s own migration, <c>Stage20AddWorkerNameFieldsAndTimestamps</c>, against data that
/// actually predates it - the thing <see cref="PostgresFixture"/> cannot prove, because every test
/// sharing that fixture sees the schema already fully migrated with an empty <c>workers</c> table, so
/// the backfill's own <c>UPDATE</c> runs over zero rows there.
///
/// <para>This suite gets its own container so it can stop the migration run one step short, insert
/// rows in the *old* shape by hand, and only then apply the migration under test - which is the only
/// way to prove a backfill actually backfills something rather than merely compiling.</para>
/// </summary>
public sealed class WorkerNameBackfillMigrationTests : IAsyncLifetime
{
    private const string PreviousMigrationId = "20260831123126_Stage20AddAccountOwnerAndContactVisibility";

    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        _dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    [Fact]
    public async Task Migrating_BackfillsATwoWordDisplayNameAndAOneWordDisplayNameAlike()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(_dataSource).Options;

        // Stop one migration short of this item's own, so `workers` is still in its pre-`20-13`
        // shape: id, tenant_id, display_name, is_active. Nothing else this schema needs for a bare
        // `workers` row to satisfy its own foreign key exists yet either, so a tenant is inserted too.
        await using (var db = new AgoCalendarDbContext(options))
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigrationId);
        }

        // Two ids minted the ordinary way - Guid.CreateVersion7(instant) - at two different, known
        // instants, so created_at/updated_at can be checked against something other than "close to
        // now". Millisecond precision only: UUIDv7's own encoded timestamp is milliseconds, so a
        // .NET DateTimeOffset carrying sub-millisecond ticks would never compare equal even for a
        // correct backfill.
        var twoWordInstant = new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.Zero).AddMilliseconds(123);
        var oneWordInstant = new DateTimeOffset(2025, 11, 3, 21, 5, 0, TimeSpan.Zero).AddMilliseconds(456);
        var twoWordId = Guid.CreateVersion7(twoWordInstant);
        var oneWordId = Guid.CreateVersion7(oneWordInstant);
        var tenantId = Guid.NewGuid();

        await using (var connection = _dataSource.CreateConnection())
        {
            await connection.OpenAsync();

            await using (var tenantCmd = connection.CreateCommand())
            {
                tenantCmd.CommandText =
                    "INSERT INTO tenants (id, name, public_key, allowed_origins, created_at) " +
                    "VALUES (@id, 'Barbershop', @key, ARRAY[]::text[], now())";
                tenantCmd.Parameters.AddWithValue("id", tenantId);
                tenantCmd.Parameters.AddWithValue("key", "shop-" + tenantId.ToString("N")[..16]);
                await tenantCmd.ExecuteNonQueryAsync();
            }

            // A realistic two-word name, and the one-word case the task explicitly calls out: a
            // one-person shop that uses its own business name as the "worker" display name, with no
            // space to (mis)split on.
            await using (var workersCmd = connection.CreateCommand())
            {
                workersCmd.CommandText =
                    """
                    INSERT INTO workers (id, tenant_id, display_name, is_active) VALUES
                        (@twoWordId, @tenantId, 'Anna Ivanova', true),
                        (@oneWordId, @tenantId, 'Barbershop', true)
                    """;
                workersCmd.Parameters.AddWithValue("twoWordId", twoWordId);
                workersCmd.Parameters.AddWithValue("oneWordId", oneWordId);
                workersCmd.Parameters.AddWithValue("tenantId", tenantId);
                await workersCmd.ExecuteNonQueryAsync();
            }
        }

        // The migration under test, plus anything after it in the same run - a fresh
        // `dotnet ef database update` from empty has to succeed end to end, not just up to this one
        // migration.
        await using (var db = new AgoCalendarDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var connection = _dataSource.CreateConnection())
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT id, last_name, first_name, display_name, display_name_is_custom, created_at, updated_at " +
                "FROM workers ORDER BY id";
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<(Guid Id, string LastName, string FirstName, string DisplayName, bool Custom, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)>();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
            }

            var twoWord = Assert.Single(rows, r => r.Id == twoWordId);
            Assert.Equal("Anna Ivanova", twoWord.LastName);
            Assert.Equal("—", twoWord.FirstName);
            Assert.Equal("Anna Ivanova", twoWord.DisplayName);
            Assert.True(twoWord.Custom);
            AssertSameMillisecond(twoWordInstant, twoWord.CreatedAt);
            AssertSameMillisecond(twoWordInstant, twoWord.UpdatedAt);

            var oneWord = Assert.Single(rows, r => r.Id == oneWordId);
            Assert.Equal("Barbershop", oneWord.LastName);
            Assert.Equal("—", oneWord.FirstName);
            Assert.Equal("Barbershop", oneWord.DisplayName);
            Assert.True(oneWord.Custom);
            AssertSameMillisecond(oneWordInstant, oneWord.CreatedAt);
            AssertSameMillisecond(oneWordInstant, oneWord.UpdatedAt);
        }
    }

    private static void AssertSameMillisecond(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.Equal(expected.ToUnixTimeMilliseconds(), actual.ToUnixTimeMilliseconds());
}
