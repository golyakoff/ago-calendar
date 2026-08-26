using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;

namespace Ago.Calendar.Integration.Tests;

/// <summary>One tenant with everything a booking needs, written through the real mappings. Every
/// test seeds its own, so no test depends on another having run first.</summary>
/// <param name="Operator">`20-06`: the seed grew an operator holding the v1
/// <see cref="Role.OperatorRoleName"/> role, because every operator-facing use case now goes through
/// a real <c>PermissionChecker</c> against real <c>roles</c>/<c>operator_roles</c> rows. A fake
/// permission checker here would have proved that the fake says yes.</param>
internal sealed record SeededTenant(
    Tenant Tenant,
    BookingCalendar Calendar,
    Worker Worker,
    Service Service,
    Customer Customer,
    Operator Operator,
    Role Role);

internal static class CalendarSeed
{
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

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
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone(zone), 10, Now);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Alex");
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));
        var customer = Customer.Register(
            new CustomerId(NewId()), tenant.Id, new PhoneNumber("+79991234567"), Now);

        var role = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var @operator = Operator.Create(
            new OperatorId(NewId()), tenant.Id, "Sam", externalSubjectId ?? $"kc-{NewId():N}");
        @operator.Grant(role);

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        db.Customers.Add(customer);
        db.Roles.Add(role);
        db.Operators.Add(@operator);
        await db.SaveChangesAsync();

        return new SeededTenant(tenant, calendar, worker, service, customer, @operator, role);
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
