using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class BookingCalendarRepository(AgoCalendarDbContext db) : IBookingCalendarRepository
{
    public Task<BookingCalendar?> GetByIdAsync(CalendarId id, CancellationToken cancellationToken) =>
        db.Calendars.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BookingCalendar>> ListPublishedAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await db.Calendars
            .Where(c => c.TenantId == tenantId && c.IsPublished)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BookingCalendar>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await db.Calendars
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken)
    {
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(BookingCalendar calendar, CancellationToken cancellationToken)
    {
        db.Calendars.Update(calendar);
        await db.SaveChangesAsync(cancellationToken);
    }
}
