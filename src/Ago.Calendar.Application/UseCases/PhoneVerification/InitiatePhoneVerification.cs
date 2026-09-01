using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <param name="CalendarId">From the route - the same "tenant comes from the calendar, never from the
/// request" shape <c>BookEvent.CalendarId</c> already establishes for the endpoint this verification
/// exists to unlock.</param>
/// <param name="Phone">Raw, as typed - normalised by <c>PhoneNumber</c> inside the handler.</param>
/// <param name="Origin">The request's own <c>Origin</c> header, or null - `20-06`'s layer-2 check,
/// identical to <c>BookEvent.Origin</c>'s own remarks on why this is a parameter rather than read from
/// an ambient context.</param>
/// <param name="CallerIp">The request's own remote address, or null when unavailable - this endpoint's
/// only caller-identifying fact, since it is reached with no session and no visitor id (see
/// <c>PhoneVerificationRateLimitOptions</c>'s own remarks). Passed in for the identical
/// Application-must-not-know-there-is-an-HTTP-request reason as <paramref name="Origin"/>.</param>
public readonly record struct InitiatePhoneVerification(
    CalendarId CalendarId, string Phone, string? Origin, string? CallerIp);

public readonly record struct InitiatedPhoneVerification(
    Guid PendingPhoneVerificationId, DateTimeOffset ExpiresAt, string DeliveryMethod);
