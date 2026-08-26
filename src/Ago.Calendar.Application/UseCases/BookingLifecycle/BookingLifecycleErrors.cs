using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// The expected failures of the three operator-facing transitions, in the same
/// <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary `20-02`'s <c>AvailabilityErrors</c> and `20-03`'s
/// <c>BookingErrors</c> established.
///
/// <para><b>Unlike `20-03`'s errors, these are precise about why.</b> That endpoint is
/// unauthenticated, so a distinguishing message would have answered questions a stranger has no
/// business asking. These three are operator-only and already past a permission check, so the caller
/// is a person who is entitled to know the difference between "somebody cancelled it a moment ago"
/// and "that visit has not happened yet" - and who cannot act correctly without it.</para>
/// </summary>
public static class BookingLifecycleErrors
{
    public static Error Forbidden(Permission permission) => new(
        "booking.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error NotFound(EventId eventId) => new(
        "booking.not_found", $"Booking {eventId.Value} does not exist.");

    /// <summary>A booking that belongs to another tenant is reported as absent rather than as
    /// forbidden - the one place these errors stay vague, because an operator of tenant A learning
    /// that an id exists in tenant B is a cross-tenant leak however politely it is worded.</summary>
    public static Error WrongTenant(EventId eventId) => NotFound(eventId);

    public static Error InvalidState(string reason) => new("booking.invalid_state", reason);

    public static Error ConcurrencyConflict(EventId eventId) => new(
        "booking.concurrency_conflict",
        $"Booking {eventId.Value} changed while you were acting on it. Reload it and try again.");
}
