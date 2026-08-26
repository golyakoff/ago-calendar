using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// Hand-written fakes, not a mocking framework. testing.md's own reasoning, and it is worth
/// restating for these particular ports: each fake below records what it was asked, which is exactly
/// what these tests assert on - "the customer row was not touched", "the claim was never attempted",
/// "the phone bucket was checked before the calendar one". A mock's verification syntax can express
/// the same things; a field holding the calls expresses them in the language the test is already
/// written in.
/// </summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Ids in call order, so a test can assert exactly which id a handler minted and when
/// without a real generator's randomness.</summary>
internal sealed class SequentialIdGenerator : IIdGenerator
{
    private int _next;

    public Guid NewId(DateTimeOffset now)
    {
        _next++;
        return new Guid(_next, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    }
}

/// <summary>
/// Records every bucket it was asked about, in order, and denies the ones a test names.
/// <see cref="Checked"/> is the assertion surface for "the phone bucket is consulted before the
/// calendar bucket" - an ordering the handler argues for and which nothing else would notice if it
/// silently reversed.
/// </summary>
internal sealed class FakeRateLimiter : IRateLimiter
{
    private readonly HashSet<string> _deniedPrefixes = new(StringComparer.Ordinal);

    public List<string> Checked { get; } = [];

    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(42);

    public void Deny(string keyPrefix) => _deniedPrefixes.Add(keyPrefix);

    public Task<RateLimitDecision> CheckAsync(
        RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken)
    {
        Checked.Add(key.Value);
        var denied = _deniedPrefixes.Any(prefix => key.Value.StartsWith(prefix, StringComparison.Ordinal));
        return Task.FromResult(new RateLimitDecision(!denied, denied ? RetryAfter : TimeSpan.Zero));
    }
}

/// <summary>
/// The port the whole item turns on. <see cref="Attempts"/> is how a test proves the negative case
/// that matters most: a rejected booking must not have reached the database at all, since reaching
/// it is what would create a lead card for a booking that never happened.
/// </summary>
internal sealed class FakeBookingStore : IBookingStore
{
    public List<BookingAttempt> Attempts { get; } = [];

    /// <summary>Null models a lost race - the ordinary outcome, not an error.</summary>
    public bool SlotIsClaimable { get; set; } = true;

    public Task<BookingConfirmation?> TryBookAsync(BookingAttempt attempt, CancellationToken cancellationToken)
    {
        Attempts.Add(attempt);

        if (!SlotIsClaimable)
        {
            return Task.FromResult<BookingConfirmation?>(null);
        }

        return Task.FromResult<BookingConfirmation?>(new BookingConfirmation(
            attempt.EventId,
            attempt.NewCustomerId,
            BookingFixtures.WorkerId,
            BookingFixtures.Slot,
            BookingFixtures.LocalDate));
    }
}

internal sealed class FakeCalendarRepository(BookingCalendar? calendar) : IBookingCalendarRepository
{
    public Task<BookingCalendar?> GetByIdAsync(CalendarId id, CancellationToken cancellationToken) =>
        Task.FromResult(calendar is not null && calendar.Id == id ? calendar : null);

    public Task<IReadOnlyList<BookingCalendar>> ListPublishedAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

internal sealed class FakeEventRepository(Event? @event) : IEventRepository
{
    public Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken) =>
        Task.FromResult(@event is not null && @event.Id == id ? @event : null);

    public Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<int> InsertAvailableSlotsAsync(IReadOnlyCollection<Event> slots, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task ReplaceDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate,
        IReadOnlyCollection<Event> replacements, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(Event @event, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "BookEventHandler must never save an Event aggregate - the claim is a compare-and-set " +
            "through IBookingStore. Reaching this is the load-mutate-save regression the item exists " +
            "to prevent.");
}

internal sealed class FakeWorkerRepository(Worker? worker) : IWorkerRepository
{
    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken) =>
        Task.FromResult(worker is not null && worker.Id == id ? worker : null);

    public Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task AddAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

internal sealed class FakeServiceRepository(Service? service) : IServiceRepository
{
    public Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken) =>
        Task.FromResult(service is not null && service.Id == id ? service : null);

    public Task<IReadOnlyList<Service>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task AddAsync(Service service, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}
