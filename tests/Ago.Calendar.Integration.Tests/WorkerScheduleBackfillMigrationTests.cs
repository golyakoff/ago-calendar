using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-14`'s own migration, <c>Stage20AddWorkerScheduleAndRemoveCalendarBuffer</c>, against data that
/// actually predates it - the same reason <see cref="WorkerNameBackfillMigrationTests"/> gets its own
/// container rather than sharing <see cref="PostgresFixture"/>: every test on that fixture sees an
/// already-fully-migrated, empty schema, so a backfill's own <c>INSERT ... SELECT</c> runs over zero
/// rows there and proves nothing about the copy itself.
/// </summary>
public sealed class WorkerScheduleBackfillMigrationTests : IAsyncLifetime
{
    private const string PreviousMigrationId = "20260901110524_Stage20AddWorkerNameFieldsAndTimestamps";

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
    public async Task Migrating_WritesAScheduleOnlyForAWorkerTheOldBufferActuallyGoverned()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(_dataSource).Options;

        // Stop one migration short of this item's own - `20-13`'s own migration is the last one
        // before it, so the schema here is exactly what a real deployment has the instant before
        // this migration runs: calendars still carry buffer_minutes, and there is no
        // worker_schedules table yet.
        await using (var db = new AgoCalendarDbContext(options))
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigrationId);
        }

        var tenantId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();

        // Governed: rules and services both present - the exact precondition
        // MaterializeAvailabilityHandler required before this item, so this is the worker whose
        // slots the old buffer actually fed.
        var governedWorkerId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        // Has working hours but offers nothing - MaterializeAvailabilityHandler.SlotLengthFor
        // returned null for him before this item (no service to derive a length from), so he never
        // had a bookable slot and gets no schedule here either.
        var noServiceWorkerId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        // Offers a service but has no working-hours rule - the handler's own `workerRules.Count == 0`
        // skip, same conclusion.
        var noRulesWorkerId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        const int calendarBufferMinutes = 7;

        await using (var connection = _dataSource.CreateConnection())
        {
            await connection.OpenAsync();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO tenants (id, name, public_key, allowed_origins, created_at)
                    VALUES (@tenantId, 'Barbershop', @key, ARRAY[]::text[], now())
                    """;
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("key", "shop-" + tenantId.ToString("N")[..16]);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO calendars (id, tenant_id, name, time_zone, buffer_minutes, is_published, created_at)
                    VALUES (@calendarId, @tenantId, 'Main', 'Europe/Moscow', @buffer, true, now())
                    """;
                cmd.Parameters.AddWithValue("calendarId", calendarId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("buffer", calendarBufferMinutes);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO workers (id, tenant_id, last_name, first_name, middle_name, display_name, display_name_is_custom, is_active, created_at, updated_at)
                    VALUES
                        (@governed, @tenantId, 'Doe', 'Alex', NULL, 'Alex Doe', false, true, now(), now()),
                        (@noService, @tenantId, 'Roe', 'Bo', NULL, 'Bo Roe', false, true, now(), now()),
                        (@noRules, @tenantId, 'Coe', 'Cy', NULL, 'Cy Coe', false, true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("governed", governedWorkerId);
                cmd.Parameters.AddWithValue("noService", noServiceWorkerId);
                cmd.Parameters.AddWithValue("noRules", noRulesWorkerId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO calendar_workers (worker_id, calendar_id) VALUES
                        (@governed, @calendarId), (@noService, @calendarId), (@noRules, @calendarId)
                    """;
                cmd.Parameters.AddWithValue("governed", governedWorkerId);
                cmd.Parameters.AddWithValue("noService", noServiceWorkerId);
                cmd.Parameters.AddWithValue("noRules", noRulesWorkerId);
                cmd.Parameters.AddWithValue("calendarId", calendarId);
                await cmd.ExecuteNonQueryAsync();
            }

            var shortServiceId = Guid.NewGuid();
            var longServiceId = Guid.NewGuid();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO services (id, tenant_id, name, duration_minutes) VALUES
                        (@shortId, @tenantId, 'Trim', 30),
                        (@longId, @tenantId, 'Colour', 90)
                    """;
                cmd.Parameters.AddWithValue("shortId", shortServiceId);
                cmd.Parameters.AddWithValue("longId", longServiceId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            // The governed worker offers both - MAX(duration_minutes) must pick 90, the longest, not
            // the first or the shortest. The no-rules worker also offers the short one, to prove
            // "offers a service" alone is not enough to earn a schedule.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO worker_services (worker_id, service_id) VALUES
                        (@governed, @shortId), (@governed, @longId), (@noRules, @shortId)
                    """;
                cmd.Parameters.AddWithValue("governed", governedWorkerId);
                cmd.Parameters.AddWithValue("noRules", noRulesWorkerId);
                cmd.Parameters.AddWithValue("shortId", shortServiceId);
                cmd.Parameters.AddWithValue("longId", longServiceId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Both the governed worker and the no-service worker have hours - only the governed one
            // also offers a service, which is what should separate them after the migration.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO working_hours_rules (id, worker_id, calendar_id, day_of_week, starts_at, ends_at) VALUES
                        (@ruleGoverned, @governed, @calendarId, 'Monday', '09:00', '18:00'),
                        (@ruleNoService, @noService, @calendarId, 'Monday', '09:00', '18:00')
                    """;
                cmd.Parameters.AddWithValue("ruleGoverned", Guid.NewGuid());
                cmd.Parameters.AddWithValue("ruleNoService", Guid.NewGuid());
                cmd.Parameters.AddWithValue("governed", governedWorkerId);
                cmd.Parameters.AddWithValue("noService", noServiceWorkerId);
                cmd.Parameters.AddWithValue("calendarId", calendarId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var beforeMigration = DateTimeOffset.UtcNow;

        // The migration under test, plus anything after it in the same run - a fresh
        // `dotnet ef database update` from empty has to succeed end to end, not just up to this one
        // migration.
        await using (var db = new AgoCalendarDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var afterMigration = DateTimeOffset.UtcNow;

        await using (var connection = _dataSource.CreateConnection())
        {
            await connection.OpenAsync();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM worker_schedules";
                var count = (long)(await cmd.ExecuteScalarAsync())!;

                // Exactly one - the governed worker only. Neither the ruleless-but-offering worker
                // nor the ruled-but-offering-nothing worker earned a row.
                Assert.Equal(1, count);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT worker_id, kind, slot_minutes, buffer_minutes, horizon_days, materialize_from, created_at, updated_at " +
                    "FROM worker_schedules";
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());

                Assert.Equal(governedWorkerId, reader.GetGuid(0));
                Assert.Equal("Weekly", reader.GetString(1));

                // The longest of the two services he offers - the exact rule
                // MaterializeAvailabilityHandler.SlotLengthFor used before this item.
                Assert.Equal(90, reader.GetInt32(2));

                // The calendar's own buffer, copied - `20-14`'s own "Decided" section, verbatim.
                Assert.Equal(calendarBufferMinutes, reader.GetInt32(3));

                Assert.Equal(30, reader.GetInt32(4));

                var materializeFrom = reader.GetFieldValue<DateOnly>(5);
                Assert.Equal(DateOnly.FromDateTime(beforeMigration.UtcDateTime), materializeFrom);

                var createdAt = reader.GetFieldValue<DateTimeOffset>(6);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(7);
                Assert.InRange(createdAt, beforeMigration.AddSeconds(-1), afterMigration.AddSeconds(1));
                Assert.InRange(updatedAt, beforeMigration.AddSeconds(-1), afterMigration.AddSeconds(1));
            }

            // And the column the schedule replaced is actually gone - not just unused.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM information_schema.columns " +
                    "WHERE table_name = 'calendars' AND column_name = 'buffer_minutes'";
                var columnCount = (long)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal(0, columnCount);
            }
        }
    }
}
