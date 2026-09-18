using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-06`'s read side, faked. <see cref="AskedFor"/> is the assertion surface for the one property
/// these handlers are responsible for: every read is scoped to a calendar the resolver already proved
/// belongs to the tenant the public key named, so a caller cannot point one at somebody else's rows
/// by supplying a different id.
/// </summary>
internal sealed class FakeBookingSurfaceReadStore : IBookingSurfaceReadStore
{
    public List<CalendarId> AskedFor { get; } = [];

    public List<BookableServiceRow> Services { get; } = [];

    public List<BookableWorkerRow> Workers { get; } = [];

    public List<OpenSlotRow> Slots { get; } = [];

    public List<(ServiceId ServiceId, WorkerId? WorkerId, DateTimeOffset NotBefore, int Limit)> SlotQueries { get; } = [];

    /// <summary>`25-145`: defaults to the identical zone <see cref="BookingFixtures.Calendar"/> itself
    /// configures, so a test that never mentions time zones still exercises real Moscow-offset
    /// conversion rather than a degenerate always-UTC one - the "the fixture that used to be implicit
    /// is now explicit but unchanged" shape this file's own sibling fakes are already held to.</summary>
    public CalendarTimeZone TimeZone { get; set; } = new("Europe/Moscow");

    public Task<IReadOnlyList<BookableServiceRow>> ListServicesAsync(
        CalendarId calendarId, CancellationToken cancellationToken)
    {
        AskedFor.Add(calendarId);
        return Task.FromResult<IReadOnlyList<BookableServiceRow>>(Services);
    }

    public Task<IReadOnlyList<BookableWorkerRow>> ListWorkersAsync(
        CalendarId calendarId, ServiceId serviceId, CancellationToken cancellationToken)
    {
        AskedFor.Add(calendarId);
        return Task.FromResult<IReadOnlyList<BookableWorkerRow>>(Workers);
    }

    public Task<IReadOnlyList<OpenSlotRow>> ListOpenSlotsAsync(
        CalendarId calendarId,
        ServiceId serviceId,
        WorkerId? workerId,
        DateTimeOffset notBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        AskedFor.Add(calendarId);
        SlotQueries.Add((serviceId, workerId, notBefore, limit));
        return Task.FromResult<IReadOnlyList<OpenSlotRow>>(Slots);
    }

    public Task<CalendarTimeZone> GetTimeZoneAsync(CalendarId calendarId, CancellationToken cancellationToken)
    {
        AskedFor.Add(calendarId);
        return Task.FromResult(TimeZone);
    }
}
