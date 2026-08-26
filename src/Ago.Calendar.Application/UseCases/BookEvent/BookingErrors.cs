using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// The booking path's expected failures, as stable codes a client branches on (api-design.md:
/// "clients branch on <c>type</c>, never on the message"). The same
/// <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary `20-02`'s <c>AvailabilityErrors</c> established.
///
/// <para><b>Deliberately vague about *why* a slot is unavailable.</b> <see cref="SlotUnavailable"/>
/// covers "somebody just took it", "it was blocked", "it already started" and "that event is not on
/// this calendar" with one code and one message. This endpoint is unauthenticated and anyone may
/// call it with any id, so a distinguishing error would answer questions a stranger has no business
/// asking - whether an event id exists, and which calendar it belongs to. The visitor's next action
/// is the same in every case: pick another slot.</para>
/// </summary>
public static class BookingErrors
{
    public static Error CalendarNotFound() => new(
        "booking.calendar_not_found",
        "That booking calendar does not exist, or is not open for bookings.");

    public static Error SlotUnavailable() => new(
        "booking.slot_unavailable",
        "Sorry, that slot has just been taken. Please choose another time.");

    public static Error InvalidPhone(string reason) => new("booking.invalid_phone", reason);

    public static Error ServiceNotOffered() => new(
        "booking.service_not_offered",
        "That service is not offered by the person working this slot.");

    /// <summary>The retry-after also travels outside the message, on
    /// <see cref="BookingOutcome.RetryAfter"/> - see that type for why. It is stated here too because
    /// a human reading a problem-details body should not have to inspect headers to learn how long to
    /// wait.</summary>
    public static Error RateLimited(TimeSpan retryAfter) => new(
        "booking.rate_limited",
        $"Too many booking attempts. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} second(s).");
}
