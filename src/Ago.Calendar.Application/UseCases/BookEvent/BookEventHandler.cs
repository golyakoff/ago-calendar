using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// A visitor takes a slot. The centre of the product, and the one path where getting concurrency
/// wrong puts two customers in one chair.
///
/// <para><b>The two-step mechanic this handler implements, and why it is the product rather than a
/// detail.</b> The customer is told, immediately and unconditionally, that they are booked. The row
/// does not say that: it moves to <see cref="EventStatus.PendingConfirmation"/> with a deadline, and
/// an operator has until then to veto it (`20-04`). Both halves are deliberate. A booking product
/// whose customer has to wait for a human to answer loses to the phone call it replaces, and a shop
/// that cannot refuse a booking will not use the product at all. The design resolves the conflict by
/// putting the uncertainty on the side that can absorb it - the business, who is looking at a queue -
/// rather than on the customer, who is looking at a page. <b>Nothing this handler returns may leak
/// the pending state</b>: see <c>BookingConfirmedResponse</c>, which has no status field and no
/// deadline field, and the test that asserts its serialised form mentions neither.</para>
///
/// <para><b>Order of operations, and why each step is where it is.</b> Cheap, local, no-I/O checks
/// first (phone shape); then the rate limiter, before any database work, so a caller who is over
/// their budget costs a Redis round trip and not a query - the same ordering
/// <c>RegisterSiteHandler</c> and <c>CreateAttachmentHandler</c> use; then the reads needed to build
/// a legal claim; then the claim itself, last, because it is the only statement that changes
/// anything.</para>
///
/// <para><b>Which of those reads are authoritative and which are courtesy.</b> The calendar and the
/// worker's service offering are facts that do not change under a booking, so reading them is safe.
/// The slot's <c>Available</c> status is not such a fact - it is exactly what is being raced for -
/// so this handler never decides on it. It is not read at all here; it lives inside the claim's own
/// <c>WHERE</c> clause (<see cref="IBookingStore"/>), where evaluating it and acting on it are one
/// operation. A pre-read of the status would be a check-then-act, and every reason `6-09` exists
/// applies to it verbatim.</para>
/// </summary>
public sealed class BookEventHandler(
    IBookingCalendarRepository calendars,
    ITenantRepository tenants,
    IEventRepository events,
    IWorkerRepository workers,
    IServiceRepository services,
    IWorkerScheduleRepository schedules,
    IBookingStore bookings,
    IRateLimiter rateLimiter,
    BookingRateLimitOptions rateLimitOptions,
    BookingOptions bookingOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<BookingOutcome> HandleAsync(BookEvent command, CancellationToken cancellationToken)
    {
        PhoneNumber phone;
        try
        {
            phone = new PhoneNumber(command.Phone ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            // The one place a domain constructor's exception is turned into an ordinary rejection.
            // A malformed phone number is a caller typing badly, not a bug - coding-style.md's rule
            // that exceptions are for the unexpected cuts the other way here, and letting an
            // ArgumentException reach the endpoint would make a 400 look like a 500 in every log.
            return BookingOutcome.Rejected(BookingErrors.InvalidPhone(exception.Message));
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);

        // An unpublished calendar is not bookable, and is reported exactly like one that does not
        // exist. A distinguishing message would let a stranger enumerate which tenants exist and
        // which of them have not launched yet.
        if (calendar is null || !calendar.IsPublished)
        {
            return BookingOutcome.Rejected(BookingErrors.CalendarNotFound());
        }

        // `20-06`, layer 2 of `5-01`'s two-layer CORS model, and the earliest point at which it can
        // be applied on this route: the calendar id in the path is the only thing that names a
        // tenant, so "which tenant is this for" is unanswerable until the line above has run - which
        // is exactly why the CORS policy itself (layer 1) can only ask the coarse question.
        //
        // Before the rate limiters, not after. A request from an origin this tenant never approved is
        // not a booking attempt, and spending the tenant's own per-calendar budget on it would let
        // any page on the internet exhaust a shop's bucket. The extra cost is one indexed primary-key
        // read on a path that has already done one.
        var tenant = await tenants.GetByIdAsync(calendar.TenantId, cancellationToken);
        if (tenant is null || !OriginPolicy.IsAcceptable(tenant, command.Origin))
        {
            // Reported as "no such calendar", not as "your origin is wrong": the caller is a stranger,
            // and a distinguishing message would confirm that the calendar exists. Same collapse the
            // unpublished case above already makes.
            return BookingOutcome.Rejected(BookingErrors.CalendarNotFound());
        }

        // Per phone before per calendar: a caller who was never going to pass their own bucket must
        // not also spend a share of the shared, coarser calendar budget finding that out
        // (RegisterSiteHandler's own reasoning, same shape).
        var phoneLimit = await rateLimiter.CheckAsync(
            PhoneBucket(calendar.TenantId, phone),
            new RateLimitRule(rateLimitOptions.PerPhoneCapacity, rateLimitOptions.PerPhoneRefillPerSecond),
            cancellationToken);
        if (!phoneLimit.Allowed)
        {
            return BookingOutcome.RateLimited(phoneLimit.RetryAfter);
        }

        var calendarLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"booking:calendar:{calendar.Id.Value}"),
            new RateLimitRule(rateLimitOptions.PerCalendarCapacity, rateLimitOptions.PerCalendarRefillPerSecond),
            cancellationToken);
        if (!calendarLimit.Allowed)
        {
            return BookingOutcome.RateLimited(calendarLimit.RetryAfter);
        }

        // Read for the worker's identity only. The status this row currently holds is deliberately
        // not consulted - see the type's own remarks.
        var slot = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (slot is null || slot.CalendarId != calendar.Id)
        {
            return BookingOutcome.Rejected(BookingErrors.SlotUnavailable());
        }

        var worker = await workers.GetByIdAsync(slot.WorkerId, cancellationToken);
        if (worker is null || !worker.IsActive || !worker.Offers(command.ServiceId))
        {
            // A real invariant, not a formality: booking a haircut with somebody who does not cut
            // hair produces an appointment nobody can honour. It is checked here rather than in the
            // claim's WHERE clause because it is a fact about the worker and the service, neither of
            // which is being raced for - unlike the slot's own status.
            return BookingOutcome.Rejected(BookingErrors.ServiceNotOffered());
        }

        var service = await services.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null || service.TenantId != calendar.TenantId)
        {
            return BookingOutcome.Rejected(BookingErrors.ServiceNotOffered());
        }

        // `20-18`: a service longer than one slot is several consecutive slots claimed as one
        // booking, not a service that is simply "not offered" (`20-14`'s own interim rule, removed by
        // this item). The worker's own schedule carries the numbers the run needs - slot length,
        // buffer, and whether the buffer counts toward the service's duration
        // (WorkerSchedule.BuffersCountTowardServiceDuration) - and without a schedule there is no grid
        // to walk, so no run can exist.
        var schedule = await schedules.GetByWorkerIdAsync(worker.Id, cancellationToken);
        if (schedule is null)
        {
            return BookingOutcome.Rejected(BookingErrors.ServiceNotOffered());
        }

        // Every row of the worker's own day, whatever its status - the courtesy read
        // ConsecutiveRunFinder needs to walk in order and see exactly where a run would break. The
        // status this handler ultimately trusts is still only the claim's own WHERE clause; this read
        // only decides *which ids to ask the claim for*, and a stale answer here costs nothing more
        // than the ordinary "slot taken" rejection any lost race already produces.
        var dayEvents = await events.ListForDayAsync(calendar.Id, worker.Id, slot.LocalDate, cancellationToken);

        var run = ConsecutiveRunFinder.FindRun(
            dayEvents,
            command.EventId,
            (int)service.Duration.TotalMinutes,
            schedule.SlotMinutes,
            schedule.BufferMinutes,
            schedule.BuffersCountTowardServiceDuration);

        if (run is null)
        {
            // No consecutive run of the length this service needs exists starting at the slot the
            // customer picked - too close to the end of the day, a middle slot already taken, or the
            // chosen slot itself is not Available by this read. Reported exactly like any other lost
            // race: the customer picked a time that turned out not to work, never a fault.
            return BookingOutcome.Rejected(BookingErrors.SlotUnavailable());
        }

        // `20-09`: the last precondition, checked immediately before the claim itself - deliberately
        // not moved earlier, alongside the phone-shape check, despite being equally cheap and equally
        // free of any check-then-act race (see the remarks below for why it is safe regardless of
        // position). Placed here so every other rejection this handler already makes - calendar not
        // found, rate-limited, slot/service mismatch - keeps its own existing precedence over this
        // one; a stranger probing an unknown calendar id still learns nothing more than
        // `booking.calendar_not_found`, never this item's own refusal, which would otherwise leak
        // "that calendar exists, you just are not verified" ahead of the identity check that already
        // owns collapsing unknown-vs-unpublished into one answer.
        //
        // Gated on RequiresVerifiedPhone, not on PhoneVerifiedAt alone - see BookEvent.RequiresVerifiedPhone's
        // own remarks for why: this item's own scope is chat-only for now, deliberately, because the public
        // booking widget has no secure way to supply this assertion yet and a self-asserted field on an
        // anonymous endpoint would be a real hole, not a convenience.
        //
        // Not a check-then-act race of the kind `status = 'Available'` genuinely is: PhoneVerifiedAt
        // is not a fact read from a row two concurrent callers contend over and that can go stale
        // between a read and a write - it is a value the caller supplies directly on this exact
        // command, with no interceding read of anything this handler does not already own. There is
        // no window for it to change out from under this decision, so refusing here is safe in a way
        // a pre-check on the slot's own live status would not be - see IBookingStore's own remarks for
        // that distinction. What this check cannot catch - a caller lying about having verified
        // something it never did - is the same trust boundary adr/0077 already accepts for the module
        // task wire itself ("authenticity is checked; the deeper claim is trusted"), not a gap specific
        // to this check.
        if (command.RequiresVerifiedPhone && command.PhoneVerifiedAt is null)
        {
            return BookingOutcome.Rejected(BookingErrors.PhoneNotVerified());
        }

        var now = clock.UtcNow;

        var confirmation = await bookings.TryBookAsync(
            new BookingAttempt(
                calendar.TenantId,
                calendar.Id,
                run,
                command.ServiceId,
                phone,
                command.DisplayName,
                new CustomerId(idGenerator.NewId(now)),
                now,
                now + bookingOptions.ConfirmationWindow,
                command.PhoneVerifiedAt),
            cancellationToken);

        // Null is the loser of the race, and it is an ordinary Tuesday: reported as a rejection the
        // visitor can act on, never logged at Error, never a 500, never an exception.
        return confirmation is null
            ? BookingOutcome.Rejected(BookingErrors.SlotUnavailable())
            : BookingOutcome.Confirmed(confirmation.Value);
    }

    /// <summary>
    /// The per-phone bucket's key, with the number hashed rather than written in plain text.
    ///
    /// <para>A rate-limit bucket is a store, and its keys are visible to anybody who can run
    /// <c>KEYS</c> against Redis or read a slow-log line. A phone number is this product's most
    /// directly identifying field, so putting it there verbatim would spread personal data into a
    /// store that <c>personal-data.md</c> did not previously list and that nothing erases on request.
    /// Hashing does not make it anonymous - the input space is small enough to enumerate, and
    /// pseudonymised data is still personal data - so the Redis row in <c>personal-data.md</c> says
    /// exactly that rather than claiming the problem away. What hashing does buy is real: the number
    /// is not readable by eye, and the bucket still expires on its own within the token bucket's own
    /// TTL.</para>
    ///
    /// <para>The tenant id is inside the hash, not beside it, so two tenants' buckets for one person
    /// are unlinkable to a reader of the key space as well as separate.</para>
    /// </summary>
    private static RateLimitKey PhoneBucket(TenantId tenantId, PhoneNumber phone)
    {
        var material = string.Create(
            CultureInfo.InvariantCulture, $"{tenantId.Value:N}:{phone.Value}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new RateLimitKey($"booking:phone:{Convert.ToHexStringLower(digest)}");
    }
}
