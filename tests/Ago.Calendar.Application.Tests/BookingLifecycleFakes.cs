using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// Permissive by default and denied by name - the shape these tests want, because every one of them
/// is about *one* permission being absent and the rest being irrelevant. A fake that started empty
/// would make every positive test list three grants it does not care about.
/// </summary>
internal sealed class FakePermissionChecker : IPermissionChecker
{
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);

    public List<(Permission Permission, TenantId TenantId)> Checked { get; } = [];

    public void Deny(Permission permission) => _denied.Add(permission.Value);

    public void Allow(Permission permission) => _denied.Remove(permission.Value);

    public Task<bool> HasPermissionAsync(
        OperatorId operatorId, TenantId tenantId, Permission permission, CancellationToken cancellationToken)
    {
        Checked.Add((permission, tenantId));
        return Task.FromResult(!_denied.Contains(permission.Value));
    }
}

/// <summary>
/// <see cref="Saved"/> and <see cref="Loaded"/> are the assertion surface for the negative cases: a
/// refused action must not have reached the database at all, and a refused *permission* must not even
/// have looked the booking up - so a caller with no right cannot use the error to learn whether an id
/// exists.
/// </summary>
internal sealed class FakeEventRepositoryWithSaves : IEventRepository
{
    private readonly IReadOnlyList<Event> _group;

    /// <summary>The ordinary, single-slot shape almost every test here still uses - one row that is
    /// its own anchor, exactly as <see cref="Event.Claim"/> now defaults it.</summary>
    public FakeEventRepositoryWithSaves(Event? booking) : this(booking is null ? [] : (IReadOnlyList<Event>)[booking])
    {
    }

    /// <summary>`20-18`: the whole run, for a test proving cancel/reject/no-show act on every row of a
    /// multi-slot booking rather than only the one the route named.</summary>
    public FakeEventRepositoryWithSaves(IReadOnlyList<Event> group) => _group = group;

    public List<Event> Saved { get; } = [];

    public List<EventId> Loaded { get; } = [];

    public List<EventId> GroupLookups { get; } = [];

    /// <summary>Models another writer committing between the load and the save - `20-01` mapped that
    /// to <see cref="EventConcurrencyConflictException"/> precisely so no handler sees an ORM
    /// type.</summary>
    public bool FailNextSaveWithConflict { get; set; }

    public Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken)
    {
        Loaded.Add(id);
        return Task.FromResult(_group.FirstOrDefault(e => e.Id == id));
    }

    public Task<IReadOnlyList<Event>> ListByBookingIdAsync(EventId bookingId, CancellationToken cancellationToken)
    {
        GroupLookups.Add(bookingId);
        return Task.FromResult<IReadOnlyList<Event>>([.. _group.Where(e => e.BookingId == bookingId)]);
    }

    public Task SaveAsync(Event @event, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The booking-lifecycle handlers save through SaveRangeAsync now, even for a single-row " +
            "booking - see IEventRepository.SaveRangeAsync's own remarks. Reaching this single-row " +
            "SaveAsync would mean a handler regressed to the pre-`20-18` shape.");

    public Task SaveRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken)
    {
        if (FailNextSaveWithConflict)
        {
            FailNextSaveWithConflict = false;
            throw new EventConcurrencyConflictException(events.First().Id);
        }

        Saved.AddRange(events);
        return Task.CompletedTask;
    }

    public Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the booking-lifecycle handlers.");

    public Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the booking-lifecycle handlers.");

    public Task<int> InsertAvailableSlotsAsync(IReadOnlyCollection<Event> slots, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the booking-lifecycle handlers.");

    public Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the booking-lifecycle handlers.");

    public Task ReplaceDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate,
        IReadOnlyCollection<Event> replacements, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the booking-lifecycle handlers.");
}

/// <summary>The shared queue, faked. <see cref="AskedFor"/> is what proves the read is tenant-scoped
/// and never narrowed to one operator or one calendar; its <c>IncludeContactData</c> flag is what
/// `20-12`'s own handler tests assert against, to prove the phone-visibility decision is made once, in
/// the handler, and handed to the read store rather than re-decided there.</summary>
internal sealed class FakePendingBookingReadStore(params PendingBookingRow[] rows) : IPendingBookingReadStore
{
    public List<(TenantId TenantId, int Limit, bool IncludeContactData)> AskedFor { get; } = [];

    public Task<IReadOnlyList<PendingBookingRow>> GetPendingForTenantAsync(
        TenantId tenantId, DateTimeOffset now, int limit, bool includeContactData, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, limit, includeContactData));
        return Task.FromResult<IReadOnlyList<PendingBookingRow>>(rows);
    }
}
