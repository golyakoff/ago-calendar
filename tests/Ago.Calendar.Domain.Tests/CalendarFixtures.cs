namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// Builders with sensible defaults, so a test names only what it cares about (testing.md). Ids are
/// <see cref="Guid.CreateVersion7"/> - the same UUID v7 shape production uses, generated here
/// directly rather than through <c>IIdGenerator</c> because a test is allowed to know the clock.
/// </summary>
internal static class CalendarFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    public static Tenant Tenant(string name = "Barbershop") =>
        Domain.Tenant.Register(new TenantId(NewId()), name, Now);

    public static BookingCalendar Calendar(
        Tenant tenant, string zone = "Europe/Moscow", int bufferMinutes = 10) =>
        BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone(zone), bufferMinutes, Now);

    public static Worker Worker(Tenant tenant, string name = "Alex") =>
        Domain.Worker.Create(new WorkerId(NewId()), tenant.Id, name);

    public static Service Service(Tenant tenant, int minutes = 45, string name = "Haircut") =>
        Domain.Service.Create(new ServiceId(NewId()), tenant.Id, name, TimeSpan.FromMinutes(minutes));

    public static Customer Customer(Tenant tenant, string phone = "+79991234567") =>
        Domain.Customer.Register(new CustomerId(NewId()), tenant.Id, new PhoneNumber(phone), Now);

    public static Event AvailableSlot(
        Tenant tenant, BookingCalendar calendar, Worker worker, DateTimeOffset? startsAt = null)
    {
        var start = startsAt ?? Now.AddDays(1);
        return Event.Materialize(
            new EventId(NewId()), tenant.Id, calendar.Id, worker.Id,
            new TimeSlot(start, start.AddMinutes(45)),
            DateOnly.FromDateTime(start.UtcDateTime),
            Now);
    }

    /// <summary>An event carried all the way to <see cref="EventStatus.Booked"/> - the starting
    /// point for every "you cannot go back" test.</summary>
    public static Event BookedSlot(Tenant tenant, BookingCalendar calendar, Worker worker, Customer customer, Service service)
    {
        var slot = AvailableSlot(tenant, calendar, worker);
        slot.Claim(customer.Id, service.Id, Now, Now.AddMinutes(15));
        slot.Confirm(Now.AddMinutes(15));
        return slot;
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);
}
