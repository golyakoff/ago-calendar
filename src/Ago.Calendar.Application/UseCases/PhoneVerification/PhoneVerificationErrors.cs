using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <summary>
/// The phone-verification path's expected failures, as stable codes a client branches on
/// (api-design.md). Shared by <c>InitiatePhoneVerificationHandler</c> and
/// <c>ConfirmPhoneVerificationHandler</c> - one small vocabulary for one closely related pair of use
/// cases, the same "PhoneVerification" feature grouping `BookingErrors`/`PublicBookingErrors` already
/// use per-feature rather than one large shared file.
///
/// <para><b>Calendar-not-found/origin collapse into one code</b>, the identical info-hiding
/// <see cref="Ago.Calendar.Application.UseCases.BookEvent.BookingErrors.CalendarNotFound"/> already
/// applies: this route is unauthenticated and reachable by anyone, so a distinguishing message would
/// answer a stranger's question about which calendars exist.</para>
/// </summary>
public static class PhoneVerificationErrors
{
    public static Error CalendarNotFound() => new(
        "phone_verification.calendar_not_found",
        "That booking calendar does not exist, or is not open for bookings.");

    public static Error InvalidPhone(string reason) => new("phone_verification.invalid_phone", reason);

    /// <summary>Unknown id, wrong tenant, or a row that belongs to a different calendar's tenant - all
    /// collapsed into one message, the identical cross-tenant info-hiding
    /// <c>ConversationErrors.PhoneVerificationNotFound</c> already applies in `ago-chat`.</summary>
    public static Error NotFound() => new(
        "phone_verification.not_found",
        "That phone verification does not exist, or has expired.");

    public static Error WrongCode() => new(
        "phone_verification.wrong_code",
        "That code is not correct. Check the number and try again.");

    public static Error Expired() => new(
        "phone_verification.expired",
        "That code has expired. Request a new one.");

    public static Error LockedOut() => new(
        "phone_verification.locked_out",
        "Too many wrong attempts. Request a new code.");

    public static Error AlreadyConsumed() => new(
        "phone_verification.already_consumed",
        "That code has already been used. Request a new one if you still need to verify.");

    public static Error RateLimited(TimeSpan retryAfter) => new(
        "phone_verification.rate_limited",
        $"Too many verification attempts. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} second(s).");
}
