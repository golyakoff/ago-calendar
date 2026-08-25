using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="BookingCalendar"/>. `20-02`'s materialiser is the caller that
/// shaped it: it needs one calendar's zone and buffer to turn wall-clock rules into instants, and it
/// needs the list of calendars worth extending - which is the published ones, not all of them.
/// </summary>
public interface IBookingCalendarRepository
{
    Task<BookingCalendar?> GetByIdAsync(CalendarId id, CancellationToken cancellationToken);

    /// <summary>Every published calendar of one tenant - the set `20-02` extends the horizon for.
    /// Tenant-scoped rather than global on purpose: a query whose <c>WHERE</c> clause cannot address
    /// another tenant's rows is the cheapest form of isolation there is (data-model.md's own note on
    /// AGO Chat's single deliberate cross-tenant read).</summary>
    Task<IReadOnlyList<BookingCalendar>> ListPublishedAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken);
}
