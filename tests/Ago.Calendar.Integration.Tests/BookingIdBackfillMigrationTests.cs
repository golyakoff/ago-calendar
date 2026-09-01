using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-18`'s own migration, <c>Stage20BookingIdAndBuffersCountTowardServiceDuration</c>, against data
/// that actually predates it - the same reason <see cref="WorkerScheduleBackfillMigrationTests"/> gets
/// its own container rather than sharing <see cref="PostgresFixture"/>: every test on that fixture sees
/// an already-fully-migrated, empty schema, so a backfill's own <c>UPDATE</c> runs over zero rows there
/// and proves nothing about the copy itself.
/// </summary>
public sealed class BookingIdBackfillMigrationTests : IAsyncLifetime
{
    private const string PreviousMigrationId = "20260901115524_Stage20AddWorkerScheduleAndRemoveCalendarBuffer";

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
    public async Task Migrating_GivesEveryAlreadyClaimedRowItsOwnIdAsItsBookingId_AndLeavesUnclaimedRowsNull()
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(_dataSource).Options;

        // Stop one migration short of this item's own - the schema here is exactly what a real
        // deployment has the instant before this migration runs: no booking_id column, no
        // buffers_count_toward_service_duration column.
        await using (var db = new AgoCalendarDbContext(options))
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigrationId);
        }

        var tenantId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var workerId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var serviceId = Guid.NewGuid();
        var customerId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var scheduleId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        // Four rows, covering every status this backfill has to tell apart: a row a claim already
        // touched (PendingConfirmation, Booked, Cancelled - customer_id set on all three, exactly the
        // fact the backfill's own predicate relies on) and one it never did (Available).
        var pendingId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var bookedId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var cancelledId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var availableId = Guid.CreateVersion7(DateTimeOffset.UtcNow);

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
                    INSERT INTO calendars (id, tenant_id, name, time_zone, is_published, created_at)
                    VALUES (@calendarId, @tenantId, 'Main', 'Europe/Moscow', true, now())
                    """;
                cmd.Parameters.AddWithValue("calendarId", calendarId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO workers (id, tenant_id, last_name, first_name, middle_name, display_name, display_name_is_custom, is_active, created_at, updated_at)
                    VALUES (@workerId, @tenantId, 'Doe', 'Alex', NULL, 'Alex Doe', false, true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("workerId", workerId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO services (id, tenant_id, name, duration_minutes)
                    VALUES (@serviceId, @tenantId, 'Haircut', 45)
                    """;
                cmd.Parameters.AddWithValue("serviceId", serviceId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO customers (id, tenant_id, phone, no_show_count, first_seen_at, last_seen_at)
                    VALUES (@customerId, @tenantId, '+79990000099', 0, now(), now())
                    """;
                cmd.Parameters.AddWithValue("customerId", customerId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO events
                        (id, tenant_id, calendar_id, worker_id, service_id, customer_id, starts_at, ends_at,
                         local_date, status, confirmation_deadline, created_at)
                    VALUES
                        (@pendingId, @tenantId, @calendarId, @workerId, @serviceId, @customerId,
                         now() + interval '1 day', now() + interval '1 day 30 minutes', current_date + 1,
                         'PendingConfirmation', now() + interval '15 minutes', now()),
                        (@bookedId, @tenantId, @calendarId, @workerId, @serviceId, @customerId,
                         now() + interval '2 day', now() + interval '2 day 30 minutes', current_date + 2,
                         'Booked', NULL, now()),
                        (@cancelledId, @tenantId, @calendarId, @workerId, @serviceId, @customerId,
                         now() + interval '3 day', now() + interval '3 day 30 minutes', current_date + 3,
                         'Cancelled', NULL, now()),
                        (@availableId, @tenantId, @calendarId, @workerId, NULL, NULL,
                         now() + interval '4 day', now() + interval '4 day 30 minutes', current_date + 4,
                         'Available', NULL, now())
                    """;
                cmd.Parameters.AddWithValue("pendingId", pendingId);
                cmd.Parameters.AddWithValue("bookedId", bookedId);
                cmd.Parameters.AddWithValue("cancelledId", cancelledId);
                cmd.Parameters.AddWithValue("availableId", availableId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("calendarId", calendarId);
                cmd.Parameters.AddWithValue("workerId", workerId);
                cmd.Parameters.AddWithValue("serviceId", serviceId);
                cmd.Parameters.AddWithValue("customerId", customerId);
                await cmd.ExecuteNonQueryAsync();
            }

            // A schedule that predates the setting, exactly as `20-14` left it - no opinion on the
            // buffer-counting question, because the question did not exist yet.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO worker_schedules
                        (id, worker_id, kind, slot_minutes, buffer_minutes, horizon_days, materialize_from, created_at, updated_at)
                    VALUES (@scheduleId, @workerId, 'Weekly', 30, 10, 30, current_date, now(), now())
                    """;
                cmd.Parameters.AddWithValue("scheduleId", scheduleId);
                cmd.Parameters.AddWithValue("workerId", workerId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // The migration under test, plus anything after it in the same run.
        await using (var db = new AgoCalendarDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var connection = _dataSource.CreateConnection())
        {
            await connection.OpenAsync();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, booking_id FROM events ORDER BY starts_at";
                await using var reader = await cmd.ExecuteReaderAsync();

                async Task<(Guid Id, Guid? BookingId)> NextAsync()
                {
                    Assert.True(await reader.ReadAsync());
                    return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
                }

                // Every already-claimed row is its own anchor - the exact default Event.Claim itself
                // falls back to for a caller (every caller before `20-18`) that never named one.
                var pending = await NextAsync();
                Assert.Equal(pendingId, pending.Id);
                Assert.Equal(pendingId, pending.BookingId);

                var booked = await NextAsync();
                Assert.Equal(bookedId, booked.Id);
                Assert.Equal(bookedId, booked.BookingId);

                var cancelled = await NextAsync();
                Assert.Equal(cancelledId, cancelled.Id);
                Assert.Equal(cancelledId, cancelled.BookingId);

                // Never claimed, never given one - inventing a booking id for a row with no booking
                // would be a fact that is not true.
                var available = await NextAsync();
                Assert.Equal(availableId, available.Id);
                Assert.Null(available.BookingId);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT buffers_count_toward_service_duration FROM worker_schedules WHERE id = @scheduleId";
                cmd.Parameters.AddWithValue("scheduleId", scheduleId);

                // A schedule that predates the setting lands on the author's own stated default -
                // buffers count - rather than an arbitrary one.
                Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
            }
        }
    }
}
