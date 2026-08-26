using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PublicBooking;

/// <summary>What a page embedding this tenant may offer: the published calendars and what can be
/// booked on each.</summary>
/// <param name="PublicKey">From the URL path, never a body - see <see cref="TenantPublicKey"/>.</param>
/// <param name="Origin">The request's own <c>Origin</c> header, or null when there is none.</param>
public readonly record struct GetBookingSurface(string PublicKey, string? Origin);

/// <param name="Services">Empty when a calendar is published but nobody on it performs anything -
/// a real configuration state, and the widget renders it as "nothing to book yet" rather than as an
/// error, because it is the shop's own doing and not the visitor's.</param>
public readonly record struct BookableCalendar(
    CalendarId CalendarId, string Name, string TimeZone, IReadOnlyList<BookableServiceRow> Services);

public readonly record struct BookingSurface(string TenantName, IReadOnlyList<BookableCalendar> Calendars);

/// <summary>
/// The first thing an embed asks for, and the only request in the flow that names the tenant rather
/// than a calendar.
///
/// <para><b>Services are returned inline rather than behind a second round trip.</b> A shop has a
/// handful of calendars and a handful of services; the list is small, it is needed immediately, and
/// splitting it would make the widget's very first interaction cost two requests on a page it is a
/// guest on. The queries stay per-calendar, so the read is still N small indexed lookups rather than
/// one join nobody can read.</para>
/// </summary>
public sealed class GetBookingSurfaceHandler(
    EmbedScopeResolver scope,
    IBookingCalendarRepository calendars,
    IBookingSurfaceReadStore surface)
{
    public async Task<Result<BookingSurface>> HandleAsync(
        GetBookingSurface query, CancellationToken cancellationToken)
    {
        var resolved = await scope.ResolveAsync(query.PublicKey, null, query.Origin, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error!.Value;
        }

        var tenant = resolved.Value.Tenant;
        var published = await calendars.ListPublishedAsync(tenant.Id, cancellationToken);

        var bookable = new List<BookableCalendar>(published.Count);
        foreach (var calendar in published)
        {
            var services = await surface.ListServicesAsync(calendar.Id, cancellationToken);
            bookable.Add(new BookableCalendar(
                calendar.Id, calendar.Name, calendar.TimeZone.Value, services));
        }

        return Result<BookingSurface>.Success(new BookingSurface(tenant.Name, bookable));
    }
}
