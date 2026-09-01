using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-03`'s first Done-when: the handler's own decisions, with every port faked. What the database
/// does under contention is the integration suite's job; what this file proves is which failure the
/// handler reports, in which order it checks things, and - the assertions that matter most - which
/// writes it declines to attempt at all.
/// </summary>
public class BookEventHandlerTests
{
    [Fact]
    public async Task ASuccessfulBooking_ClaimsTheSlotAndCarriesTheCustomerTheStoreShouldUpsert()
    {
        var world = new World();

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.Error);

        var attempt = Assert.Single(world.Bookings.Attempts);

        // The tenant is resolved from the calendar, never taken from the request - the endpoint is
        // unauthenticated, so anything the caller supplied about a tenant would be a claim, not a
        // fact.
        Assert.Equal(BookingFixtures.TenantId, attempt.TenantId);
        Assert.Equal(BookingFixtures.CalendarId, attempt.CalendarId);
        Assert.Equal([BookingFixtures.EventId], attempt.EventIds);

        // Normalised on the way in, so "+7 (999) 000-00-01" and "+79990000001" reach one lead card.
        Assert.Equal(BookingFixtures.Phone, attempt.Phone.Value);

        // The deadline is an absolute instant computed here from IClock plus the configured window -
        // never a duration handed to the adapter to add to a clock the test cannot control.
        Assert.Equal(BookingFixtures.Now + world.Booking.ConfirmationWindow, attempt.ConfirmationDeadline);
        Assert.Equal(BookingFixtures.Now, attempt.Now);
    }

    [Fact]
    public async Task ABooking_FromAnOriginThisTenantApproved_Succeeds()
    {
        var world = new World();

        var outcome = await world.HandleAsync(
            BookingFixtures.Command(origin: BookingFixtures.ApprovedOrigin));

        Assert.True(outcome.IsSuccess);
    }

    [Fact]
    public async Task ABooking_FromAnotherTenantsApprovedOrigin_IsRefusedBeforeAnyRateLimitIsSpent()
    {
        // `20-06`, layer 2 on the write path. The origin belongs to *some* tenant, so layer 1 would
        // have let the browser read the response - this is the check that makes it a tenant boundary.
        var world = new World();

        var outcome = await world.HandleAsync(BookingFixtures.Command(origin: "https://other.example"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.calendar_not_found", outcome.Error!.Value.Code);

        // Two things that must not have happened. Nothing was written, and - the reason the check
        // sits before the limiters - no page on the internet spent a share of this shop's own
        // per-calendar budget finding out it was refused.
        Assert.Empty(world.Bookings.Attempts);
        Assert.Empty(world.Limiter.Checked);
    }

    [Fact]
    public async Task ABooking_WithNoOriginHeader_Succeeds()
    {
        // Deliberate asymmetry - see OriginPolicy. A caller with no Origin is not a browser, so the
        // attack layer 2 exists to stop is not available to them; and `21-01`'s channel adapter is a
        // legitimate caller with no browser in the path at all.
        var world = new World();

        Assert.True((await world.HandleAsync(BookingFixtures.Command(origin: null))).IsSuccess);
    }

    [Fact]
    public async Task ABooking_AgainstATenantWithNoApprovedOriginsAtAll_IsRefusedForABrowser()
    {
        // The safe default made visible: a tenant nobody has configured an origin for is bookable by
        // a script and by nobody's page.
        var world = new World(tenant: BookingFixtures.Tenant([]));

        var outcome = await world.HandleAsync(
            BookingFixtures.Command(origin: BookingFixtures.ApprovedOrigin));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.calendar_not_found", outcome.Error!.Value.Code);
    }

    [Fact]
    public async Task APhoneTypedAnyWay_ReachesTheSameLeadCard()
    {
        var world = new World();

        await world.HandleAsync(BookingFixtures.Command(phone: "+7 (999) 000-00-01"));

        Assert.Equal(BookingFixtures.Phone, Assert.Single(world.Bookings.Attempts).Phone.Value);
    }

    [Fact]
    public async Task ALostRace_IsAnOrdinaryRejection_NotAnException()
    {
        var world = new World();
        world.Bookings.SlotIsClaimable = false;

        // No try/catch here, deliberately: if this ever throws, the test fails with the exception,
        // which is exactly the regression worth catching. `4-01`'s precedent in one line - a
        // rows-affected count of zero is a normal outcome, not an error.
        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.slot_unavailable", outcome.Error!.Value.Code);
        Assert.Null(outcome.RetryAfter);

        // It still reached the store: losing is something only the database can determine.
        Assert.Single(world.Bookings.Attempts);
    }

    [Fact]
    public async Task AnEventOnAnotherCalendar_IsRejectedWithoutTouchingTheCustomerRow()
    {
        // The handler's slot read comes back null, which is the shape both "no such event" and
        // "that event belongs to somebody else's calendar" arrive in - the handler collapses them
        // into one answer on purpose.
        var world = new World(slotExists: false);

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.slot_unavailable", outcome.Error!.Value.Code);

        // The assertion the Done-when asks for: a rejected booking never reaches the store, so no
        // lead card is written for a booking that did not happen. That is a data-minimisation
        // property, not only a tidiness one - see IBookingStore.
        Assert.Empty(world.Bookings.Attempts);
    }

    [Fact]
    public async Task AnUnpublishedCalendar_IsIndistinguishableFromOneThatDoesNotExist()
    {
        var unpublished = new World(calendar: BookingFixtures.Calendar(published: false));
        var missing = new World(calendarExists: false);

        var unpublishedOutcome = await unpublished.HandleAsync(BookingFixtures.Command());
        var missingOutcome = await missing.HandleAsync(BookingFixtures.Command());

        // Same code, same message. A stranger with a list of guids must not be able to learn which
        // tenants exist and which have not launched yet.
        Assert.Equal(missingOutcome.Error!.Value, unpublishedOutcome.Error!.Value);
        Assert.Equal("booking.calendar_not_found", unpublishedOutcome.Error!.Value.Code);
        Assert.Empty(unpublished.Bookings.Attempts);
        Assert.Empty(missing.Bookings.Attempts);
    }

    [Fact]
    public async Task AMalformedPhone_IsRejectedBeforeAnythingElseIsAsked()
    {
        var world = new World();

        var outcome = await world.HandleAsync(BookingFixtures.Command(phone: "12345"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.invalid_phone", outcome.Error!.Value.Code);

        // Before the rate limiter, so a caller typing badly does not spend a token; before the
        // store, so nothing is written. Also proves the ArgumentException PhoneNumber throws is
        // turned into a rejection rather than escaping the layer as a 500.
        Assert.Empty(world.Limiter.Checked);
        Assert.Empty(world.Bookings.Attempts);
    }

    /// <summary>`20-09`'s own Done-when: "a claim carrying no verification assertion is refused." The
    /// actual, enforced Calendar-side gate this item adds - moved from `Confirm` to immediately before
    /// `Claim` ever runs, per the backlog item's "Decided 2026-08-31" section. Checked last, after
    /// every other rejection this handler already makes (unlike the phone-shape check, which is cheap
    /// enough to fail fast on) - see <c>BookEventHandler</c>'s own remarks on why: so an unrelated
    /// rejection (an unknown calendar, a rate limit) keeps its own existing precedence and this one
    /// never leaks "that calendar exists, you are just unverified" ahead of it.</summary>
    [Fact]
    public async Task ANullPhoneVerifiedAt_IsRejectedImmediatelyBeforeTheClaim()
    {
        var world = new World();

        var outcome = await world.HandleAsync(BookingFixtures.Command(phoneVerified: false));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.phone_not_verified", outcome.Error!.Value.Code);

        // Every earlier check still ran (both rate-limit buckets were consulted, exactly as they
        // would be for a verified attempt) - this is the last gate, not a shortcut around the others.
        Assert.Equal(2, world.Limiter.Checked.Count);
        Assert.Empty(world.Bookings.Attempts);
    }

    /// <summary>The phone-verification gate never pre-empts an unrelated rejection that would have
    /// happened anyway - an unknown calendar is still reported as `booking.calendar_not_found`, not
    /// `booking.phone_not_verified`, for an unverified caller exactly as for a verified one.</summary>
    [Fact]
    public async Task AnUnverifiedBooking_AgainstAnUnknownCalendar_StillReportsCalendarNotFound()
    {
        var world = new World(calendarExists: false);

        var outcome = await world.HandleAsync(BookingFixtures.Command(phoneVerified: false));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.calendar_not_found", outcome.Error!.Value.Code);
    }

    /// <summary>The other half of the same Done-when: a verified attempt carries the assertion through
    /// to the store unchanged, which is what lets <c>BookingStore</c>'s own SQL snapshot it onto the
    /// customer row.</summary>
    [Fact]
    public async Task AVerifiedPhone_CarriesItsOwnVerificationTimestampToTheStore()
    {
        var world = new World();
        var verifiedAt = BookingFixtures.Now.AddDays(-3);

        var outcome = await world.HandleAsync(BookingFixtures.Command(phoneVerifiedAt: verifiedAt));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(verifiedAt, Assert.Single(world.Bookings.Attempts).PhoneVerifiedAt);
    }

    [Fact]
    public async Task AServiceTheWorkerDoesNotPerform_IsRejected()
    {
        var world = new World();
        var somethingElse = new ServiceId(new Guid("99999999-9999-9999-9999-999999999999"));

        var outcome = await world.HandleAsync(BookingFixtures.Command(serviceId: somethingElse));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.service_not_offered", outcome.Error!.Value.Code);
        Assert.Empty(world.Bookings.Attempts);
    }

    /// <summary>
    /// `20-18`'s own headline scenario: `20-14`'s "not offered" stopgap is gone, and a service that
    /// needs more than one slot claims the consecutive run that satisfies it. Three 30-minute slots
    /// with a 10-minute buffer between them; a 70-minute service with the tenant's default
    /// (buffers count) needs exactly two - the item's own 70/30/10 worked example.
    /// </summary>
    [Fact]
    public async Task AServiceLongerThanOneSlot_ClaimsTheConsecutiveRunItNeeds()
    {
        var service = Service.Create(BookingFixtures.ServiceId, BookingFixtures.TenantId, "Colour", TimeSpan.FromMinutes(70));
        var schedule = BookingFixtures.Schedule(slotMinutes: 30, bufferMinutes: 10);
        var day = BookingFixtures.ConsecutiveSlots(count: 3, slotMinutes: 30, bufferMinutes: 10);
        var worker = BookingFixtures.WorkerOffering(service);
        var world = new World(service: service, worker: worker, schedule: schedule, day: day);

        var outcome = await world.HandleAsync(BookingFixtures.Command(serviceId: service.Id));

        Assert.True(outcome.IsSuccess);
        var attempt = Assert.Single(world.Bookings.Attempts);

        // Two slots, not three: 70 minutes needs 30+10+30=70 when the buffer counts, which is this
        // fixture's default - the run stops at the second slot, the third is left untouched.
        Assert.Equal([day[0].Id, day[1].Id], attempt.EventIds);
    }

    /// <summary>The same 70/30/10 numbers, buffers *not* counting - the item's own other worked
    /// example, needing a third slot instead.</summary>
    [Fact]
    public async Task AServiceLongerThanOneSlot_WithBuffersNotCounting_NeedsOneMoreSlot()
    {
        var service = Service.Create(BookingFixtures.ServiceId, BookingFixtures.TenantId, "Colour", TimeSpan.FromMinutes(70));
        var schedule = BookingFixtures.Schedule(slotMinutes: 30, bufferMinutes: 10, buffersCountTowardServiceDuration: false);
        var day = BookingFixtures.ConsecutiveSlots(count: 3, slotMinutes: 30, bufferMinutes: 10);
        var worker = BookingFixtures.WorkerOffering(service);
        var world = new World(service: service, worker: worker, schedule: schedule, day: day);

        var outcome = await world.HandleAsync(BookingFixtures.Command(serviceId: service.Id));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(
            [day[0].Id, day[1].Id, day[2].Id],
            Assert.Single(world.Bookings.Attempts).EventIds);
    }

    /// <summary>The middle slot of what would otherwise be a valid run is already gone (taken,
    /// blocked, or claimed by somebody else) - the run cannot be completed, so the booking is refused
    /// exactly as any other lost race is, never as a fault.</summary>
    [Fact]
    public async Task ARunWhoseMiddleSlotIsAlreadyTaken_IsRejected()
    {
        var service = Service.Create(BookingFixtures.ServiceId, BookingFixtures.TenantId, "Colour", TimeSpan.FromMinutes(70));
        var schedule = BookingFixtures.Schedule(slotMinutes: 30, bufferMinutes: 10);
        var day = BookingFixtures.ConsecutiveSlots(count: 3, slotMinutes: 30, bufferMinutes: 10).ToList();
        day[1].Claim(BookingFixtures.CustomerId, service.Id, BookingFixtures.Now, BookingFixtures.Now.AddMinutes(15));
        var worker = BookingFixtures.WorkerOffering(service);
        var world = new World(service: service, worker: worker, schedule: schedule, day: day);

        var outcome = await world.HandleAsync(BookingFixtures.Command(serviceId: service.Id));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.slot_unavailable", outcome.Error!.Value.Code);
        Assert.Empty(world.Bookings.Attempts);
    }

    /// <summary>A worker with no schedule at all has no grid to walk - `20-18`'s own precondition for
    /// run-finding, and the same rejection a schedule-less worker's services already produce
    /// elsewhere.</summary>
    [Fact]
    public async Task AWorkerWithNoSchedule_IsNotBookable()
    {
        var world = new World(noSchedule: true);

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.service_not_offered", outcome.Error!.Value.Code);
        Assert.Empty(world.Bookings.Attempts);
    }

    [Fact]
    public async Task AnInactiveWorker_IsNotBookable()
    {
        var service = BookingFixtures.HaircutService();
        var world = new World(service: service, worker: BookingFixtures.WorkerOffering(service, active: false));

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.False(outcome.IsSuccess);
        Assert.Empty(world.Bookings.Attempts);
    }

    [Fact]
    public async Task ADeniedPhoneBucket_RateLimitsAndCarriesARetryAfter()
    {
        var world = new World();
        world.Limiter.Deny("booking:phone:");

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("booking.rate_limited", outcome.Error!.Value.Code);

        // Structured, not only in the message: this is what becomes a Retry-After header, and it is
        // the whole reason this handler returns BookingOutcome instead of Result<T>.
        Assert.Equal(world.Limiter.RetryAfter, outcome.RetryAfter);
        Assert.Empty(world.Bookings.Attempts);

        // The calendar bucket was never consulted: a caller who could not pass their own bucket must
        // not also spend a share of the shared one finding that out.
        Assert.Single(world.Limiter.Checked);
    }

    [Fact]
    public async Task ADeniedCalendarBucket_RateLimits()
    {
        var world = new World();
        world.Limiter.Deny("booking:calendar:");

        var outcome = await world.HandleAsync(BookingFixtures.Command());

        Assert.Equal("booking.rate_limited", outcome.Error!.Value.Code);
        Assert.Empty(world.Bookings.Attempts);
        Assert.Equal(2, world.Limiter.Checked.Count);
    }

    [Fact]
    public async Task ThePhoneBucketIsCheckedBeforeTheCalendarBucket()
    {
        var world = new World();

        await world.HandleAsync(BookingFixtures.Command());

        Assert.Collection(
            world.Limiter.Checked,
            key => Assert.StartsWith("booking:phone:", key, StringComparison.Ordinal),
            key => Assert.StartsWith("booking:calendar:", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePhoneNeverAppearsInARateLimitKey()
    {
        var world = new World();

        await world.HandleAsync(BookingFixtures.Command());

        // A rate-limit bucket is a store, and its key space is readable by anyone who can run KEYS
        // against Redis. The phone is hashed with the tenant id so a reader of that key space sees
        // neither the number nor which numbers two tenants have in common. Hashing does not make it
        // anonymous - the input space is enumerable - which is why personal-data.md lists the bucket
        // rather than claiming the problem away.
        var phoneKey = Assert.Single(world.Limiter.Checked, key => key.StartsWith("booking:phone:", StringComparison.Ordinal));
        Assert.DoesNotContain("999", phoneKey, StringComparison.Ordinal);
        Assert.DoesNotContain(BookingFixtures.Phone, phoneKey, StringComparison.Ordinal);
        Assert.DoesNotContain(BookingFixtures.TenantId.Value.ToString(), phoneKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSamePhoneAtTwoTenants_GetsTwoDifferentBuckets()
    {
        // One person booking at two shops within a minute is ordinary. One shared bucket would let
        // one shop's customer throttle another's.
        var first = new World();
        var second = new World(calendar: OtherTenantsCalendar());

        await first.HandleAsync(BookingFixtures.Command());
        await second.HandleAsync(BookingFixtures.Command());

        Assert.NotEqual(first.Limiter.Checked[0], second.Limiter.Checked[0]);
    }

    private static BookingCalendar OtherTenantsCalendar()
    {
        var calendar = BookingCalendar.Create(
            BookingFixtures.CalendarId,
            new TenantId(new Guid("77777777-7777-7777-7777-777777777777")),
            "Other", new CalendarTimeZone("Europe/Moscow"), BookingFixtures.Now);
        calendar.Publish();
        return calendar;
    }

    /// <summary>The handler plus its fakes, assembled once. A sealed local world rather than a
    /// builder: every test wants the same shape with at most one thing different.</summary>
    private sealed class World
    {
        private readonly BookEventHandler _handler;

        public World(
            BookingCalendar? calendar = null,
            bool calendarExists = true,
            bool slotExists = true,
            Worker? worker = null,
            Service? service = null,
            Tenant? tenant = null,
            // `20-18`: BookEventHandler now needs the worker's own schedule to run-find, and the day's
            // own rows to walk. Defaulting to one slot's worth of each keeps every pre-`20-18` test
            // that never mentions either unchanged - the fixture that used to be implicit is now
            // explicit but produces the identical one-slot world. `noSchedule: true` is the one
            // exception, for the test proving a schedule-less worker is not bookable at all.
            WorkerSchedule? schedule = null,
            bool noSchedule = false,
            IReadOnlyList<Event>? day = null)
        {
            var resolvedService = service ?? BookingFixtures.HaircutService();
            var resolvedCalendar = calendar ?? BookingFixtures.Calendar();
            var resolvedSchedule = noSchedule ? null : schedule ?? BookingFixtures.Schedule();
            var resolvedDay = day ?? (slotExists ? [BookingFixtures.AvailableSlot()] : []);

            _handler = new BookEventHandler(
                new FakeCalendarRepository(calendarExists ? resolvedCalendar : null),
                // The tenant follows the calendar, because that is how the handler resolves it: the
                // endpoint is unauthenticated, so the calendar in the route is the only thing that
                // names a tenant. A fixture whose tenant did not match its calendar would make every
                // booking fail for a reason no production caller can produce.
                new FakeTenantRepository(
                    tenant ?? BookingFixtures.Tenant(tenantId: resolvedCalendar.TenantId)),
                new FakeEventRepository(resolvedDay),
                new FakeWorkerRepository(worker ?? BookingFixtures.WorkerOffering(resolvedService)),
                new FakeServiceRepository(resolvedService),
                new FakeWorkerScheduleRepository(resolvedSchedule),
                Bookings,
                Limiter,
                new BookingRateLimitOptions(),
                Booking,
                new SequentialIdGenerator(),
                new FakeClock(BookingFixtures.Now));
        }

        public FakeBookingStore Bookings { get; } = new();

        public FakeRateLimiter Limiter { get; } = new();

        public BookingOptions Booking { get; } = new();

        public Task<BookingOutcome> HandleAsync(BookEvent command) =>
            _handler.HandleAsync(command, CancellationToken.None);
    }
}
