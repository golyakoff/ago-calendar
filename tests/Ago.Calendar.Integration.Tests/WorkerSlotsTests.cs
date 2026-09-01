using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.WorkerSlots;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-15`'s materialised slot view, against a real Postgres: the table a tenant reaches from a
/// worker's card to see what their own schedule actually produced.
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody, the same convention
/// <c>SharedPendingQueueTests</c> uses.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkerSlotsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 5, 31);

    [Fact]
    public async Task TheTableShowsEveryStatus_WithLocalDateWeekdayTimeAndStatus()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var available = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));
        var blocked = Event.BlockOut(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(
                new DateTimeOffset(2026, 5, 12, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 12, 13, 30, 0, TimeSpan.Zero)),
            new DateOnly(2026, 5, 12), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.AddRange(available, blocked);
            await db.SaveChangesAsync();
        }

        var rows = await SlotsAsync(seed.Operator.Id, seed.Tenant.Id, seed.Worker.Id);

        Assert.Equal(2, rows.Count);
        var availableRow = rows.Single(row => row.EventId == available.Id);
        Assert.Equal(new DateOnly(2026, 5, 12), availableRow.LocalDate);
        Assert.Equal(DayOfWeek.Tuesday, availableRow.LocalDate.DayOfWeek);
        Assert.Equal(EventStatus.Available, availableRow.Status);

        var blockedRow = rows.Single(row => row.EventId == blocked.Id);
        Assert.Equal(EventStatus.Blocked, blockedRow.Status);
        // A closure is not a service - Event.ServiceId's own remarks.
        Assert.Null(blockedRow.ServiceId);
    }

    [Fact]
    public async Task AnOccupiedSlot_ShowsNameAndPhone_ToACallerHoldingCustomerRead()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await ABookedSlotAsync(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));

        var rows = await SlotsAsync(seed.Operator.Id, seed.Tenant.Id, seed.Worker.Id);

        var row = Assert.Single(rows);
        Assert.Equal(booking.Id, row.EventId);
        Assert.Equal(seed.Customer.Id, row.CustomerId);
        Assert.NotNull(row.Phone);
        Assert.Equal(seed.Customer.Phone, row.Phone!.Value);
    }

    [Fact]
    public async Task TheSameOccupiedSlot_ShowsCustomerIdButHidesNameAndPhone_ToACallerWithoutCustomerRead()
    {
        // `20-12`'s own Done-when, restated for this screen: one underlying booking, two callers, two
        // different answers about the same two fields - and CustomerId is what proves this is a
        // masking of the *same* row, not a different, filtered one.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await ABookedSlotAsync(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));
        var stranger = await AnOperatorWithConfigureButNotCustomerReadAsync(seed.Tenant.Id);

        var rows = await SlotsAsync(stranger, seed.Tenant.Id, seed.Worker.Id);

        var row = Assert.Single(rows);
        Assert.Equal(booking.Id, row.EventId);
        // The slot is still shown as occupied - CustomerId is never gated, because it is not personal
        // data - but the two fields that are stay null, never a stand-in value.
        Assert.Equal(seed.Customer.Id, row.CustomerId);
        Assert.Null(row.CustomerDisplayName);
        Assert.Null(row.Phone);
    }

    [Fact]
    public async Task AFreeSlot_HasNoCustomerIdEitherWay_SoItIsNeverMistakenForAWithheldOne()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var free = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));
        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(free);
            await db.SaveChangesAsync();
        }

        var stranger = await AnOperatorWithConfigureButNotCustomerReadAsync(seed.Tenant.Id);
        var rows = await SlotsAsync(stranger, seed.Tenant.Id, seed.Worker.Id);

        var row = Assert.Single(rows);
        Assert.Null(row.CustomerId);
        Assert.Null(row.Phone);
    }

    [Fact]
    public async Task ARange_ExcludesRowsOutsideIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var inside = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));
        var beforeRange = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
        var afterRange = CalendarSeed.Slot(seed, new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.AddRange(inside, beforeRange, afterRange);
            await db.SaveChangesAsync();
        }

        var rows = await SlotsAsync(seed.Operator.Id, seed.Tenant.Id, seed.Worker.Id);

        Assert.Equal(new[] { inside.Id }, rows.Select(row => row.EventId));
    }

    [Fact]
    public async Task AnotherTenantsWorker_IsNotFound_RatherThanAnEmptyList()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var result = await new GetWorkerSlotsHandler(
                new WorkerSlotReadStore(fixture.DataSource), new WorkerRepository(db), new PermissionChecker(db))
            .HandleAsync(
                new GetWorkerSlots(mine.Operator.Id, mine.Tenant.Id, theirs.Worker.Id, From, To),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("worker_slots.worker_not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AnOperatorWithoutCalendarConfigure_GetsNothingAtAll()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var stranger = await AnOperatorWithoutCalendarConfigureAsync(seed.Tenant.Id);

        await using var db = fixture.CreateDbContext();
        var result = await new GetWorkerSlotsHandler(
                new WorkerSlotReadStore(fixture.DataSource), new WorkerRepository(db), new PermissionChecker(db))
            .HandleAsync(
                new GetWorkerSlots(stranger, seed.Tenant.Id, seed.Worker.Id, From, To), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("worker_slots.forbidden", result.Error!.Value.Code);
    }

    /// <summary>
    /// **The critical proof**: not merely that this store's own C# happens to null out the contact
    /// columns, but that the unpermitted SQL constant genuinely never reaches <c>customers</c> at the
    /// database level. A Postgres role granted <c>SELECT</c> on <c>events</c> and <c>services</c> but
    /// not on <c>customers</c> can run <see cref="WorkerSlotReadStore.GetForWorkerAsync"/> with
    /// <c>includeContactData: false</c> to completion - which would be impossible if that query ever
    /// touched <c>customers</c>, privileged connection or not, since Postgres enforces table grants
    /// regardless of what a query planner might otherwise be able to skip. The same role then fails
    /// with <c>42501 insufficient_privilege</c> the moment it asks for contact data, which is what
    /// proves the split is real - the two constants are not parallel dead code where one of them
    /// happens to never run.
    /// </summary>
    [Fact]
    public async Task TheUnpermittedQuery_TrulyNeverReadsCustomers_NotJustMasksTheResultInCSharp()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedSlotAsync(seed, new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero));

        var (roleName, password) = await ARoleWithoutCustomersSelectAsync();
        var restrictedConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Username = roleName,
            Password = password,
        }.ConnectionString;

        await using var restrictedDataSource = NpgsqlDataSource.Create(restrictedConnectionString);
        var store = new WorkerSlotReadStore(restrictedDataSource);

        // The unpermitted query succeeds under a role that is denied SELECT on customers - which it
        // could not do if the query ever named that table.
        var rows = await store.GetForWorkerAsync(
            seed.Tenant.Id, seed.Worker.Id, From, To, includeContactData: false, CancellationToken.None);
        Assert.Single(rows);

        // The *same* role, asked for contact data, hits the table it was denied - proving
        // SqlWithContactData really does join customers, so the split above is not two branches that
        // happen to produce the same SQL.
        var denied = await Assert.ThrowsAsync<PostgresException>(() => store.GetForWorkerAsync(
            seed.Tenant.Id, seed.Worker.Id, From, To, includeContactData: true, CancellationToken.None));
        Assert.Equal("42501", denied.SqlState);
    }

    private async Task<(string RoleName, string Password)> ARoleWithoutCustomersSelectAsync()
    {
        var roleName = $"restricted_{CalendarSeed.NewId():N}"[..28];
        var password = CalendarSeed.NewId().ToString("N");

        await using var admin = fixture.DataSource.CreateConnection();
        await admin.OpenAsync();

        await using (var createRole = admin.CreateCommand())
        {
            // A throwaway login role, scoped to this one Testcontainers-owned database and dropped
            // with it - never a real credential, and nothing this test writes down survives the
            // container.
            createRole.CommandText = $"""CREATE ROLE "{roleName}" LOGIN PASSWORD '{password}';""";
            await createRole.ExecuteNonQueryAsync();
        }

        await using (var grant = admin.CreateCommand())
        {
            grant.CommandText = $"""
                GRANT CONNECT ON DATABASE "{admin.Database}" TO "{roleName}";
                GRANT USAGE ON SCHEMA public TO "{roleName}";
                GRANT SELECT ON events, services TO "{roleName}";
                """;
            await grant.ExecuteNonQueryAsync();
        }

        return (roleName, password);
    }

    /// <summary>
    /// One tracked <see cref="AgoCalendarDbContext"/> across all three saves, the same shape
    /// <c>SharedPendingQueueTests.APendingBookingAsync</c> uses - <c>Event</c>'s <c>xmin</c>
    /// concurrency token is a shadow property EF Core keeps on the tracked entry, not a field on the
    /// CLR object, so a fresh context in between saves has no way to know the row's current version
    /// and every write after the first would be rejected as a lost race that never happened. Found by
    /// running this test for real: the first version opened a second context for the follow-up saves
    /// and every one of them threw <c>DbUpdateConcurrencyException</c>.
    /// </summary>
    private async Task<Event> ABookedSlotAsync(SeededTenant seed, DateTimeOffset startsAt)
    {
        var slot = CalendarSeed.Slot(seed, startsAt);

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();

        slot.Claim(seed.Customer.Id, seed.Service.Id, Now, Now.AddMinutes(30));
        slot.ClearDomainEvents();
        await db.SaveChangesAsync();

        slot.Confirm(Now.AddMinutes(31));
        slot.ClearDomainEvents();
        await db.SaveChangesAsync();

        return slot;
    }

    private async Task<OperatorId> AnOperatorWithConfigureButNotCustomerReadAsync(TenantId tenantId)
    {
        var role = Role.Create(
            new RoleId(CalendarSeed.NewId()), tenantId, "Scheduler-only", [Permission.CalendarConfigure]);
        var @operator = Operator.Create(new OperatorId(CalendarSeed.NewId()), tenantId, "Casey");
        @operator.Grant(role);

        await using var db = fixture.CreateDbContext();
        db.Roles.Add(role);
        db.Operators.Add(@operator);
        await db.SaveChangesAsync();

        return @operator.Id;
    }

    private async Task<OperatorId> AnOperatorWithoutCalendarConfigureAsync(TenantId tenantId)
    {
        var role = Role.Create(
            new RoleId(CalendarSeed.NewId()), tenantId, "Dispatcher-only",
            [Permission.BookingReject, Permission.CustomerRead]);
        var @operator = Operator.Create(new OperatorId(CalendarSeed.NewId()), tenantId, "Sam");
        @operator.Grant(role);

        await using var db = fixture.CreateDbContext();
        db.Roles.Add(role);
        db.Operators.Add(@operator);
        await db.SaveChangesAsync();

        return @operator.Id;
    }

    private async Task<IReadOnlyList<WorkerSlotRow>> SlotsAsync(
        OperatorId operatorId, TenantId tenantId, WorkerId workerId, DateOnly? from = null, DateOnly? to = null)
    {
        await using var db = fixture.CreateDbContext();
        var result = await new GetWorkerSlotsHandler(
                new WorkerSlotReadStore(fixture.DataSource), new WorkerRepository(db), new PermissionChecker(db))
            .HandleAsync(
                new GetWorkerSlots(operatorId, tenantId, workerId, from ?? From, to ?? To), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }
}
