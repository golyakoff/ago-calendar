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

    /// <summary>
    /// Every calendar of one tenant, published or not - the configuration console's own list
    /// (`20-06`). Separate from <see cref="ListPublishedAsync"/> rather than a boolean parameter on
    /// it, because the two have opposite defaults: a public read that accidentally included
    /// unpublished calendars would expose a shop's unlaunched surface, and a console that silently
    /// hid them would make the publish switch invisible to the only person who can flip it. A flag
    /// makes the wrong value a typo; two methods make it a different call.
    /// </summary>
    Task<IReadOnlyList<BookingCalendar>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken);

    /// <summary>Persists a change to an existing calendar - its name, its buffer, or whether it is
    /// published.</summary>
    Task SaveAsync(BookingCalendar calendar, CancellationToken cancellationToken);
}
