using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// What the migration actually produced, read back from the live database rather than from the
/// migration's own source. `4-01` set the precedent for the index half of this: an index is only
/// real if the query planner picks it, and reading the CreateIndex call proves nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaAndIndexTests(PostgresFixture fixture)
{
    [Fact]
    public void TheMigrationLeftNoPendingModelChanges()
    {
        // The from-scratch migration run happened in the fixture. This asserts the other half: the
        // model and the schema agree, so nobody has edited a configuration without adding a
        // migration for it - the single most common way a deployed database drifts three migrations
        // behind its code (testing.md's own deployment-smoke incident).
        using var db = fixture.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task TheAvailabilityIndex_IsUsedByTheQueryItExistsFor()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // A partial index is only chosen when the planner believes it is worth it, and on a table of
        // five rows it never will be - it will seq-scan and be right to. `enable_seqscan = off`
        // makes the planner state its preference among the indexes it actually has, which is the
        // question being asked here: does an index exist that *can* serve this predicate? Whether it
        // is faster than a scan is a measurement, and this test does not pretend to make it.
        var plan = await ExplainAsync(
            $"""
            EXPLAIN
            SELECT id FROM events
            WHERE calendar_id = '{seed.Calendar.Id.Value}'
              AND status = 'Available'
              AND starts_at >= now()
            ORDER BY starts_at
            LIMIT 20
            """);

        Assert.Contains("ix_events_available", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePendingConfirmationIndex_IsUsedByTheSweepQuery()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // `20-04`'s auto-confirm sweep, written here only so its index has a reader to be proven
        // against - the job itself belongs to that item.
        var plan = await ExplainAsync(
            $"""
            EXPLAIN
            SELECT id FROM events
            WHERE tenant_id = '{seed.Tenant.Id.Value}'
              AND status = 'PendingConfirmation'
              AND confirmation_deadline <= now()
            """);

        Assert.Contains("ix_events_pending_confirmation", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNoOverlapConstraint_ExistsAsAnExclusionConstraint()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT contype::text
            FROM pg_constraint
            WHERE conname = 'ex_events_worker_no_overlap'
            """,
            connection);

        // 'x' is Postgres's own code for an exclusion constraint. Asserted on the catalogue rather
        // than on the DDL text, because what matters is what the database ended up with.
        Assert.Equal("x", await command.ExecuteScalarAsync() as string);
    }

    [Fact]
    public async Task TheLeadCardIsUniquePerTenant_AndOnlyPerTenant()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        // The same person books at two different shops: two cards, no collision. This is the half of
        // the index that a global unique constraint would have got wrong.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(mine.Customer.Phone, theirs.Customer.Phone);
            Assert.NotEqual(mine.Customer.Id, theirs.Customer.Id);
            Assert.Equal(1, await db.Customers.CountAsync(
                c => c.Phone == mine.Customer.Phone && c.TenantId == mine.Tenant.Id));
            Assert.Equal(1, await db.Customers.CountAsync(
                c => c.Phone == mine.Customer.Phone && c.TenantId == theirs.Tenant.Id));
        }

        // The same person twice inside one tenant: one card, and the storage says so even when the
        // application forgot to look first.
        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(Customer.Register(
                new CustomerId(CalendarSeed.NewId()), mine.Tenant.Id, mine.Customer.Phone, CalendarSeed.Now));

            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(
                PostgresErrorCodes.UniqueViolation,
                Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
    }

    [Fact]
    public async Task EveryMappingRoundTrips_IncludingTheOnesThatAreNotTimestamps()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, "America/New_York");
        var rule = WorkingHoursRule.For(
            new WorkingHoursRuleId(CalendarSeed.NewId()), seed.Worker, seed.Calendar,
            DayOfWeek.Tuesday, new TimeOnly(9, 30), new TimeOnly(18, 0));

        // `20-14`: a cycle schedule, chosen over a weekly one here specifically because it is the
        // half of the mapping with the most columns (five nullable ones) to round-trip.
        var schedule = WorkerSchedule.CreateCycle(
            new WorkerScheduleId(CalendarSeed.NewId()), seed.Worker.Id,
            anchor: new DateOnly(2026, 3, 2), workingDays: 2, restDays: 2,
            startsAt: new TimeOnly(8, 0), endsAt: new TimeOnly(20, 0),
            slotMinutes: 45, bufferMinutes: 10, horizonDays: 60,
            materializeFrom: new DateOnly(2026, 3, 2), CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.WorkingHoursRules.Add(rule);
            db.WorkerSchedules.Add(schedule);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();

        // Wall clock survives as wall clock - `time`, not a timestamptz that silently acquired an
        // offset on the way in.
        var storedRule = await new WorkingHoursRuleRepository(reader)
            .ListForCalendarAsync(seed.Calendar.Id, CancellationToken.None);
        Assert.Equal(new TimeOnly(9, 30), Assert.Single(storedRule).StartsAt);
        Assert.Equal(DayOfWeek.Tuesday, storedRule[0].DayOfWeek);

        // The IANA zone survives as a name.
        var storedCalendar = await new BookingCalendarRepository(reader)
            .GetByIdAsync(seed.Calendar.Id, CancellationToken.None);
        Assert.Equal("America/New_York", storedCalendar!.TimeZone.Value);

        // `20-14`: the cycle's own nullable columns, and the enum stored as its name.
        var storedSchedule = await new WorkerScheduleRepository(reader)
            .GetByWorkerIdAsync(seed.Worker.Id, CancellationToken.None);
        Assert.Equal(ScheduleKind.Cycle, storedSchedule!.Kind);
        Assert.Equal(new DateOnly(2026, 3, 2), storedSchedule.CycleAnchor);
        Assert.Equal(2, storedSchedule.CycleWorkingDays);
        Assert.Equal(2, storedSchedule.CycleRestDays);
        Assert.Equal(new TimeOnly(8, 0), storedSchedule.CycleStartsAt);
        Assert.Equal(new TimeOnly(20, 0), storedSchedule.CycleEndsAt);
        Assert.Equal(45, storedSchedule.SlotMinutes);
        Assert.Equal(10, storedSchedule.BufferMinutes);
        Assert.Equal(60, storedSchedule.HorizonDays);

        // `22-05`/`adr/0093`: the seed's own projection row - a plain `text[]`, not the value
        // converter the removed `roles.permissions` column used to need, and CalendarSeed's own
        // remarks explain why it grants the whole v1 catalogue.
        var storedPermissions = await new RoleAssignmentProjectionStore(reader)
            .GetPermissionsAsync(seed.OperatorId, seed.Tenant.Id, CancellationToken.None);
        Assert.Contains(Permission.BookingConfirm.Value, storedPermissions);
        Assert.Equal(7, storedPermissions.Count);

        // The worker's own two joins come back loaded, which is what makes WorksIn/Offers safe to
        // ask after a load.
        var storedWorker = await new WorkerRepository(reader).GetByIdAsync(seed.Worker.Id, CancellationToken.None);
        Assert.True(storedWorker!.WorksIn(seed.Calendar.Id));
        Assert.True(storedWorker.Offers(seed.Service.Id));

        // And the service duration is minutes in, minutes out.
        var storedService = await new ServiceRepository(reader).GetByIdAsync(seed.Service.Id, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(45), storedService!.Duration);
    }

    [Fact]
    public async Task TheWorkerDayIndex_IsUsedByTheQueryTheNonDestructiveRuleTurnsOn()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // `20-02`: "which of this worker's days already have rows" is the query that decides whether
        // a day is regenerated, so it is the one that must not degrade into a scan of every event
        // this worker has ever had. Same enable_seqscan trick and the same caveat as above - this
        // asks whether a usable index exists, not whether it is faster on five rows.
        var plan = await ExplainAsync(
            $"""
            EXPLAIN
            SELECT DISTINCT local_date FROM events
            WHERE calendar_id = '{seed.Calendar.Id.Value}'
              AND worker_id = '{seed.Worker.Id.Value}'
              AND local_date BETWEEN DATE '2026-05-01' AND DATE '2026-05-31'
            """);

        Assert.Contains("ix_events_worker_day", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMaterializedLocalDates_CountsEveryStatus_IncludingCancelled()
    {
        // This replaced `20-01`'s GetMaterializedHorizon_IgnoresCancelledRows, and it asserts the
        // *opposite* rule about cancelled rows - deliberately, because the question changed with the
        // granularity. An instant-valued horizon asked "how far does this worker's occupied time
        // reach", and a cancellation frees the worker's time, so it was no evidence. A day-valued
        // check asks "was this day generated", and a cancelled row is proof that it was. Counting it
        // is what stops the materialiser writing a fresh grid on top of a day's own history
        // (adr/0053).
        var seed = await CalendarSeed.WriteAsync(fixture);
        var start = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
        var available = CalendarSeed.Slot(seed, start);
        var cancelled = CalendarSeed.Slot(seed, start.AddDays(1).AddHours(2));

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EventRepository(db);
            await repository.AddRangeAsync([available, cancelled], CancellationToken.None);
            cancelled.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
            cancelled.Cancel(CalendarSeed.Now.AddMinutes(1));
            await repository.SaveAsync(cancelled, CancellationToken.None);
        }

        await using var reader = fixture.CreateDbContext();
        var days = await new EventRepository(reader).ListMaterializedLocalDatesAsync(
            seed.Calendar.Id, seed.Worker.Id,
            available.LocalDate, cancelled.LocalDate, CancellationToken.None);

        Assert.Equal([available.LocalDate, cancelled.LocalDate], days.Order());
    }

    [Fact]
    public async Task ListMaterializedLocalDates_ForAWorkerWithNothingMaterialised_IsEmpty()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var days = await new EventRepository(db).ListMaterializedLocalDatesAsync(
            seed.Calendar.Id, seed.Worker.Id,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), CancellationToken.None);

        Assert.Empty(days);
    }

    [Fact]
    public async Task ListMaterializedLocalDates_IsBoundedByTheWindow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var inside = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero));
        var outside = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero));

        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([inside, outside], CancellationToken.None);
        }

        await using var reader = fixture.CreateDbContext();
        var days = await new EventRepository(reader).ListMaterializedLocalDatesAsync(
            seed.Calendar.Id, seed.Worker.Id,
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), CancellationToken.None);

        // A day outside the window is not reported, so a horizon that has been pushed far past the
        // current one cannot make every day inside it look done.
        Assert.Equal([inside.LocalDate], days);
    }

    private async Task<string> ExplainAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Its own statement, and SET LOCAL so it dies with this transaction rather than leaking a
        // planner setting into every later test sharing this container.
        await using (var disableScans = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await disableScans.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();

        var plan = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }
}
