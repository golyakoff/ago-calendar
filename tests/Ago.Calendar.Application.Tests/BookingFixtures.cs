using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// One tenant, one published calendar, one worker offering one service, and one available slot -
/// the smallest world a booking needs.
///
/// <para>Phone numbers here are invented and belong to nobody: <c>+7999...</c> numbers are used
/// throughout this repository's fixtures for the same reason a test never uses a colleague's - a
/// public repository must not carry a real person's contact details, and a phone number is this
/// product's most directly identifying field (<c>personal-data.md</c>).</para>
/// </summary>
internal static class BookingFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    public static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    public static readonly CalendarId CalendarId = new(new Guid("22222222-2222-2222-2222-222222222222"));
    public static readonly WorkerId WorkerId = new(new Guid("33333333-3333-3333-3333-333333333333"));
    public static readonly ServiceId ServiceId = new(new Guid("44444444-4444-4444-4444-444444444444"));
    public static readonly EventId EventId = new(new Guid("55555555-5555-5555-5555-555555555555"));

    public static readonly DateOnly LocalDate = new(2026, 5, 4);

    /// <summary>Two hours after <see cref="Now"/>, so the slot is genuinely in the future - the
    /// claim's own <c>starts_at &gt; now</c> predicate would otherwise be trivially satisfied by an
    /// accident of the fixture.</summary>
    public static readonly TimeSlot Slot = new(Now.AddHours(2), Now.AddHours(2).AddMinutes(45));

    public const string Phone = "+79990000001";

    /// <summary>`20-06`: the tenant `BookEventHandler` now loads to run layer 2's origin check. The
    /// default list contains the one origin the tests treat as approved; an empty list is what
    /// "nobody's page may embed this" looks like.</summary>
    public const string ApprovedOrigin = "https://shop.example";

    public static Tenant Tenant(IEnumerable<string>? allowedOrigins = null, TenantId? tenantId = null) =>
        Domain.Tenant.Register(
            tenantId ?? TenantId,
            "Barbershop",
            new TenantPublicKey("barbershop"),
            Now,
            allowedOrigins ?? [ApprovedOrigin]);

    public static BookingCalendar Calendar(bool published = true)
    {
        var calendar = BookingCalendar.Create(
            CalendarId, TenantId, "Main", new CalendarTimeZone("Europe/Moscow"), 10, Now);
        if (published)
        {
            calendar.Publish();
        }

        return calendar;
    }

    public static Service HaircutService() =>
        Service.Create(ServiceId, TenantId, "Haircut", TimeSpan.FromMinutes(45));

    public static Worker WorkerOffering(Service service, bool active = true)
    {
        var worker = Worker.Create(WorkerId, TenantId, "Alex");
        worker.JoinCalendar(Calendar());
        worker.Offer(service);
        if (!active)
        {
            worker.Deactivate();
        }

        return worker;
    }

    public static Event AvailableSlot() =>
        Event.Materialize(EventId, TenantId, CalendarId, WorkerId, Slot, LocalDate, Now);

    /// <param name="origin">`20-06`. Null by default, which is what a non-browser caller sends and
    /// what <c>OriginPolicy</c> deliberately allows - see its remarks for why an absent
    /// <c>Origin</c> is not a rejection on this product's booking surface.</param>
    public static BookEvent Command(string? phone = null, ServiceId? serviceId = null, string? origin = null) =>
        new(CalendarId, EventId, serviceId ?? ServiceId, phone ?? Phone, "Anna", origin);

    /// <summary>`20-04`: a second tenant, so "another tenant's booking" is a real id rather than a
    /// missing one - the two must produce the same answer, and only a real one proves it.</summary>
    public static readonly TenantId OtherTenantId = new(new Guid("88888888-8888-8888-8888-888888888888"));

    public static readonly CustomerId CustomerId = new(new Guid("99999999-9999-9999-9999-999999999999"));

    /// <summary>A booking sitting in the veto window - what the queue shows and what the sweep will
    /// confirm if nobody acts.</summary>
    public static Event PendingBooking(TenantId? tenantId = null)
    {
        var booking = Event.Materialize(
            EventId, tenantId ?? TenantId, CalendarId, WorkerId, Slot, LocalDate, Now);
        booking.Claim(CustomerId, ServiceId, Now, Now.AddMinutes(15));
        booking.ClearDomainEvents();
        return booking;
    }

    /// <summary>The same booking after its window closed with nobody acting.</summary>
    public static Event ConfirmedBooking(TenantId? tenantId = null)
    {
        var booking = PendingBooking(tenantId);
        booking.Confirm(Now.AddMinutes(15));
        booking.ClearDomainEvents();
        return booking;
    }
}
