using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;

namespace Ago.Calendar.Integration.Tests;

/// <summary>One tenant with everything a booking needs, written through the real mappings. Every
/// test seeds its own, so no test depends on another having run first.</summary>
/// <param name="OperatorId">`22-05`/`adr/0093`: derived, not a row - see
/// <see cref="OperatorId.FromExternalSubjectId"/>. Every operator-facing use case now goes through a
/// real <c>PermissionChecker</c> against a real <c>role_assignment_projections</c> row this seed
/// writes directly, through the same public <c>RoleAssignmentProjectionStore</c> adapter production
/// code uses - not a fake permission checker, which would only prove that the fake says yes.</param>
internal sealed record SeededTenant(
    Tenant Tenant,
    BookingCalendar Calendar,
    Worker Worker,
    Service Service,
    Customer Customer,
    OperatorId OperatorId,
    string ExternalSubjectId);

internal static class CalendarSeed
{
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The v1 permission set - `Ago.Calendar.Domain.Permission`'s own catalogue, unchanged
    /// by `22-05` (only where it is granted moved). Every seeded operator holds all seven, the same
    /// "one seeded role, everything a small business's one person needs" shape the removed
    /// <c>Role.SeedOperatorRole</c> used to establish.</summary>
    internal static readonly string[] AllPermissions =
    [
        Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.BookingCancel.Value,
        Permission.BookingMarkNoShow.Value, Permission.CustomerRead.Value, Permission.CustomerEdit.Value,
        Permission.CalendarConfigure.Value,
    ];

    /// <param name="publicKey">`20-06`. Unique per seeded tenant by default, because
    /// <c>ux_tenants_public_key</c> is real and two tests seeding "barbershop" in the same container
    /// would collide - a named parameter for the tests that care what the key is.</param>
    /// <param name="allowedOrigins">Empty by default, which is the safe default the aggregate
    /// documents: no browser origin may embed this tenant until somebody says one may.</param>
    public static async Task<SeededTenant> WriteAsync(
        PostgresFixture fixture,
        string zone = "Europe/Moscow",
        string? publicKey = null,
        IEnumerable<string>? allowedOrigins = null,
        string? externalSubjectId = null)
    {
        var tenant = Tenant.Register(
            new TenantId(NewId()),
            "Barbershop",
            new TenantPublicKey(publicKey ?? $"shop-{NewId():N}"[..24]),
            Now,
            allowedOrigins);
        var calendar = BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone(zone), Now);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Doe", "Alex", null, Now);
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));
        var customer = Customer.Register(
            new CustomerId(NewId()), tenant.Id, new PhoneNumber("+79991234567"), Now);

        // `22-05`/`adr/0093`: no `Operator`/`Role` to create or grant any more - a projection row is
        // the whole fact. Written through the real adapter, not a fake, so a test seeded through here
        // exercises the identical `PermissionChecker` path a real `RoleAssignmentsChanged` delivery
        // would have populated.
        var subject = externalSubjectId ?? $"kc-{NewId():N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        db.Customers.Add(customer);

        var projections = new RoleAssignmentProjectionStore(db);
        await projections.StageAsync(operatorId, tenant.Id, subject, AllPermissions, Now, CancellationToken.None);

        await db.SaveChangesAsync();

        return new SeededTenant(tenant, calendar, worker, service, customer, operatorId, subject);
    }

    /// <summary>Gives the seeded worker the same wall-clock window on every named day. Wall clock,
    /// not instants - see <see cref="WorkingHoursRule"/>; the conversion is the materialiser's
    /// single job.</summary>
    public static async Task AddWorkingHoursAsync(
        PostgresFixture fixture,
        SeededTenant seed,
        TimeOnly opensAt,
        TimeOnly closesAt,
        params DayOfWeek[] days)
    {
        await using var db = fixture.CreateDbContext();
        foreach (var day in days)
        {
            db.WorkingHoursRules.Add(WorkingHoursRule.For(
                new WorkingHoursRuleId(NewId()), seed.Worker, seed.Calendar, day, opensAt, closesAt));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// `20-14`: the seeded worker's own schedule - slot length, buffer, horizon and cursor, all of
    /// which the materialiser now reads from here instead of from the calendar's old buffer and the
    /// worker's longest offered service.
    /// </summary>
    /// <param name="materializeFrom"><see cref="DateOnly.MinValue"/> by default, which puts the
    /// cursor safely in the past for every calendar's zone so <c>firstDay</c> always resolves to
    /// "today" - matching `20-02`'s own pre-`20-14` behaviour, where there was no cursor at all and a
    /// run always started from today.</param>
    public static async Task<WorkerSchedule> AddWeeklyScheduleAsync(
        PostgresFixture fixture,
        SeededTenant seed,
        int horizonDays,
        int slotMinutes = 45,
        int bufferMinutes = 10,
        DateOnly? materializeFrom = null)
    {
        var schedule = WorkerSchedule.CreateWeekly(
            new WorkerScheduleId(NewId()), seed.Worker.Id,
            slotMinutes, bufferMinutes, horizonDays, materializeFrom ?? DateOnly.MinValue, Now);

        await using var db = fixture.CreateDbContext();
        db.WorkerSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    /// <summary>`20-14`: a cycle schedule for the seeded worker - N working days, M resting, from
    /// <paramref name="anchor"/>. See <see cref="AddWeeklyScheduleAsync"/> for the same
    /// <paramref name="materializeFrom"/> default and why it is safe.</summary>
    public static async Task<WorkerSchedule> AddCycleScheduleAsync(
        PostgresFixture fixture,
        SeededTenant seed,
        DateOnly anchor,
        int workingDays,
        int restDays,
        TimeOnly startsAt,
        TimeOnly endsAt,
        int horizonDays,
        int slotMinutes = 45,
        int bufferMinutes = 10,
        DateOnly? materializeFrom = null)
    {
        var schedule = WorkerSchedule.CreateCycle(
            new WorkerScheduleId(NewId()), seed.Worker.Id,
            anchor, workingDays, restDays, startsAt, endsAt,
            slotMinutes, bufferMinutes, horizonDays, materializeFrom ?? DateOnly.MinValue, Now);

        await using var db = fixture.CreateDbContext();
        db.WorkerSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    public static DayOfWeek[] EveryDay =>
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public static Event Slot(SeededTenant seed, DateTimeOffset startsAt, int minutes = 45) =>
        Event.Materialize(
            new EventId(NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(minutes)),
            DateOnly.FromDateTime(startsAt.UtcDateTime),
            Now);

    public static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
