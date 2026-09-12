using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>`25-63`: records what was staged, without EfOutboxWriter's own DbContext/transaction
/// behaviour - that guarantee is proven against real Postgres in
/// Ago.Calendar.Integration.Tests (testing.md: never mock the database for a guarantee the schema
/// itself provides). The same shape `Ago.Chat.Application.Tests.Fakes.FakeOutboxWriter` already
/// establishes.</summary>
internal sealed class FakeOutboxWriter : IOutboxWriter
{
    public List<EventEnvelope> Enqueued { get; } = [];

    public void Enqueue(EventEnvelope envelope, string? traceContext = null) => Enqueued.Add(envelope);
}

/// <summary>A real Guid each call, deterministic enough for these tests (which assert shape, not a
/// specific id) - the identical shape `Ago.Chat.Application.Tests.Fakes.FakeIdGenerator`
/// establishes.</summary>
internal sealed class FakeIdGenerator : IIdGenerator
{
    public Guid NewId(DateTimeOffset now) => Guid.NewGuid();
}

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
    public List<(TenantId TenantId, int Limit, bool IncludeContactData, bool Mask)> AskedFor { get; } = [];

    public Task<IReadOnlyList<PendingBookingRow>> GetPendingForTenantAsync(
        TenantId tenantId, DateTimeOffset now, int limit, bool includeContactData, bool mask,
        CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, limit, includeContactData, mask));
        return Task.FromResult<IReadOnlyList<PendingBookingRow>>(rows);
    }
}

/// <summary>`23-12`: a tenant's own rung, faked - defaults to <see cref="ContactVisibility.Visible"/>
/// unless a test stages otherwise, the identical default the real store's own "missing row" case
/// returns.</summary>
internal sealed class FakeContactVisibilityProjectionStore(
    ContactVisibility rung = ContactVisibility.Visible) : IContactVisibilityProjectionStore
{
    public ContactVisibility Rung { get; set; } = rung;

    public List<TenantId> AskedFor { get; } = [];

    public Task<ContactVisibility> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        AskedFor.Add(tenantId);
        return Task.FromResult(Rung);
    }

    public Task StageAsync(
        TenantId tenantId, ContactVisibility newRung, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by any handler test - only the consumer stages.");
}
