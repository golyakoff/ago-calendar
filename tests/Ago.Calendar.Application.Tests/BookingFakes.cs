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
            attempt.EventIds[0],
            attempt.EventIds,
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
        Task.FromResult<IReadOnlyList<BookingCalendar>>(
            calendar is not null && calendar.TenantId == tenantId && calendar.IsPublished ? [calendar] : []);

    public Task<IReadOnlyList<BookingCalendar>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookingCalendar>>(
            calendar is not null && calendar.TenantId == tenantId ? [calendar] : []);

    public Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(BookingCalendar calendar, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

/// <summary>
/// `20-06`: <c>BookEventHandler</c> resolves the tenant to run layer 2's origin check, and the
/// public-read handlers resolve one by public key. One fake serves both, holding at most one tenant -
/// which is all a handler test needs, because "another tenant's" is expressed by a key or an origin
/// that does not match rather than by a second row.
/// </summary>
internal sealed class FakeTenantRepository(Tenant? tenant) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken) =>
        Task.FromResult(tenant is not null && tenant.Id == id ? tenant : null);

    public Task<IReadOnlyList<TenantId>> ListIdsAsync(
        TenantId? after, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by these handlers.");

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by these handlers.");

    public Task<Tenant?> FindByPublicKeyAsync(TenantPublicKey publicKey, CancellationToken cancellationToken) =>
        Task.FromResult(tenant is not null && tenant.PublicKey == publicKey ? tenant : null);

    public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken) =>
        Task.FromResult(tenant is not null && tenant.Allows(origin));

    public Task SaveAsync(Tenant tenant, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// `20-18` widened this from a single optional slot to the worker's whole day: <c>BookEventHandler</c>
/// now calls <see cref="ListForDayAsync"/> to let <c>ConsecutiveRunFinder</c> walk the day the same way
/// production does, so a test that wants a multi-slot run has to hand the fake every row of that run,
/// not just the one the customer picked.
/// </summary>
internal sealed class FakeEventRepository(IReadOnlyList<Event> events) : IEventRepository
{
    public FakeEventRepository(Event? single)
        : this(single is null ? [] : (IReadOnlyList<Event>)[single])
    {
    }

    public Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken) =>
        Task.FromResult(events.FirstOrDefault(e => e.Id == id));

    public Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<int> InsertAvailableSlotsAsync(IReadOnlyCollection<Event> slots, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Event>>(
            [.. events.Where(e => e.CalendarId == calendarId && e.WorkerId == workerId && e.LocalDate == localDate)]);

    public Task ReplaceDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate,
        IReadOnlyCollection<Event> replacements, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(Event @event, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "BookEventHandler must never save an Event aggregate - the claim is a compare-and-set " +
            "through IBookingStore. Reaching this is the load-mutate-save regression the item exists " +
            "to prevent.");

    public Task<IReadOnlyList<Event>> ListByBookingIdAsync(EventId bookingId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

/// <summary>`20-18`: the port <c>BookEventHandler</c> reads to run-find a multi-slot booking. Holds at
/// most one schedule, matching every other fake's "one row is all a handler test needs" shape.</summary>
internal sealed class FakeWorkerScheduleRepository(WorkerSchedule? schedule) : IWorkerScheduleRepository
{
    public Task<WorkerSchedule?> GetByWorkerIdAsync(WorkerId workerId, CancellationToken cancellationToken) =>
        Task.FromResult(schedule is not null && schedule.WorkerId == workerId ? schedule : null);

    public Task<IReadOnlyList<WorkerSchedule>> ListForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task AddAsync(WorkerSchedule schedule, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(WorkerSchedule schedule, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

internal sealed class FakeWorkerRepository(Worker? worker) : IWorkerRepository
{
    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken) =>
        Task.FromResult(worker is not null && worker.Id == id ? worker : null);

    public Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<IReadOnlyList<Worker>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Worker>>(
            worker is not null && worker.TenantId == tenantId ? [worker] : []);

    public Task AddAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task SaveAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");

    public Task<bool> DeleteIfNeverBookedAsync(WorkerId id, TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by BookEventHandler.");
}

/// <summary>`20-10`: <see cref="PhoneVerificationAssertionResolver"/>'s own returning-customer
/// shortcut. Defaults to "no returning customer" - the identical no-op-by-default shape every other
/// fake in this file uses, so a test that never mentions phone verification exercises the exact same
/// path it always did.</summary>
internal sealed class FakeCustomerRepository(Customer? customer = null) : ICustomerRepository
{
    public Task<Customer?> FindByPhoneAsync(TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken) =>
        Task.FromResult(
            customer is not null && customer.TenantId == tenantId && customer.Phone.Value == phone.Value
                ? customer
                : null);

    public Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by PhoneVerificationAssertionResolver.");

    public Task AddAsync(Customer customer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by PhoneVerificationAssertionResolver.");

    public Task SaveAsync(Customer customer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by PhoneVerificationAssertionResolver.");
}

/// <summary>`20-10`: backs both <see cref="PhoneVerificationAssertionResolver"/>'s own fresh-proof
/// lookup (read-only there - defaults to "no such row" for the identical no-op-by-default reason
/// <see cref="FakeCustomerRepository"/> gives) and <c>InitiatePhoneVerificationHandler</c>/
/// <c>ConfirmPhoneVerificationHandler</c>'s own real reads and writes, which do need
/// <see cref="SaveAsync"/> to actually persist - unlike the other fakes in this file, whose "not
/// reached" throw only ever needs to hold for <c>BookEventHandler</c>'s own call graph.</summary>
internal sealed class FakePendingPhoneVerificationRepository(PendingPhoneVerification? verification = null)
    : IPendingPhoneVerificationRepository
{
    private PendingPhoneVerification? _verification = verification;

    public List<PendingPhoneVerification> Saved { get; } = [];

    public Task<PendingPhoneVerification?> GetByIdAsync(
        PendingPhoneVerificationId id, CancellationToken cancellationToken) =>
        Task.FromResult(_verification is not null && _verification.Id == id ? _verification : null);

    public Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken)
    {
        _verification = verification;
        Saved.Add(verification);
        return Task.CompletedTask;
    }
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
