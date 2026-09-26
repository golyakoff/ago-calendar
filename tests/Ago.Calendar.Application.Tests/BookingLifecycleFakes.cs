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

/// <summary>
/// `26-208`: the reschedule store, faked. <see cref="Requests"/> is the assertion surface for the
/// positive cases (what run was claimed, which person/service/origin it carried, which one event was
/// staged) and <see cref="Called"/> is the surface for the negative ones (a refused reschedule must
/// not have reached the store at all). Models the real store's own contract: on the success path it
/// stages the request's pre-built <c>BookingRescheduled</c> envelope onto <paramref name="outbox"/>,
/// exactly as <c>BookingRescheduleStore</c> does inside its transaction - so a handler test can assert
/// "exactly one BookingRescheduled, and nothing else, is staged" without a database.
/// </summary>
internal sealed class FakeBookingRescheduleStore(FakeOutboxWriter outbox) : IBookingRescheduleStore
{
    public List<BookingRescheduleRequest> Requests { get; } = [];

    public bool Called => Requests.Count > 0;

    /// <summary>Default true - the ordinary "the new run was claimable" path. Set false to model the
    /// claim losing the race, the ordinary outcome that leaves the old booking untouched.</summary>
    public bool SlotIsClaimable { get; set; } = true;

    /// <summary>Set to make the store throw the given exception instead of returning - models the
    /// cancel-half racing another writer (the handler maps these to invalid_state / concurrency).</summary>
    public Exception? ThrowOnReschedule { get; set; }

    public Task<bool> TryRescheduleAsync(BookingRescheduleRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (ThrowOnReschedule is not null)
        {
            throw ThrowOnReschedule;
        }

        if (!SlotIsClaimable)
        {
            return Task.FromResult(false);
        }

        // The real store stages exactly this one envelope, on the success path, inside its
        // transaction - modelled here so the caller's fake outbox shows the same one row.
        outbox.Enqueue(request.RescheduledEvent);
        return Task.FromResult(true);
    }
}

/// <summary>
/// `26-208`: the event repository the reschedule handler reads through - <see cref="GetByIdAsync"/>
/// (the old booking, and the target slot), <see cref="ListByBookingIdAsync"/> (the old run) and
/// <see cref="ListForDayAsync"/> (the target day's grid). Every write method throws: the reschedule
/// handler never saves an <see cref="Event"/> itself - the atomic write is the store's - so reaching
/// one is the load-mutate-save regression this fake exists to catch, the same shape
/// <c>BookingFakes.FakeEventRepository</c> uses for <c>BookEventHandler</c>.
/// </summary>
internal sealed class FakeRescheduleEventRepository(IReadOnlyList<Event> events) : IEventRepository
{
    public List<EventId> Loaded { get; } = [];

    public Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken)
    {
        Loaded.Add(id);
        return Task.FromResult(events.FirstOrDefault(e => e.Id == id));
    }

    public Task<IReadOnlyList<Event>> ListByBookingIdAsync(EventId bookingId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Event>>(
            [.. events.Where(e => e.BookingId == bookingId).OrderBy(e => e.StartsAt)]);

    public Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Event>>(
            [.. events
                .Where(e => e.CalendarId == calendarId && e.WorkerId == workerId && e.LocalDate == localDate)
                .OrderBy(e => e.StartsAt)]);

    public Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by RescheduleBookingHandler.");

    public Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by RescheduleBookingHandler.");

    public Task<int> InsertAvailableSlotsAsync(IReadOnlyCollection<Event> slots, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by RescheduleBookingHandler.");

    public Task ReplaceDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate,
        IReadOnlyCollection<Event> replacements, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by RescheduleBookingHandler.");

    public Task SaveAsync(Event @event, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "RescheduleBookingHandler must never save an Event aggregate - the cancel-old + claim-new " +
            "write is the store's, in one transaction. Reaching this is the regression the item forbids.");

    public Task SaveRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by RescheduleBookingHandler - the store owns the write.");
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
