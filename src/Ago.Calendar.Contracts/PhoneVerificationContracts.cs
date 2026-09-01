namespace Ago.Calendar.Contracts;

/// <summary>
/// `20-10`: the public booking widget's own phone-verification round trip - initiate, then confirm -
/// wired to <c>POST /api/v1/calendars/{calendarId}/phone-verifications</c> and
/// <c>POST /api/v1/calendars/{calendarId}/phone-verifications/{id}/confirm</c>.
/// </summary>
public sealed record InitiatePhoneVerificationRequest(string? Phone);

public sealed record InitiatedPhoneVerificationResponse(
    Guid PendingPhoneVerificationId, DateTimeOffset ExpiresAt, string DeliveryMethod);

public sealed record ConfirmPhoneVerificationRequest(string? Code);

/// <param name="ProofToken">Carry this, together with <paramref name="PendingPhoneVerificationId"/>,
/// into <see cref="BookEventRequest.PhoneVerificationId"/>/<see cref="BookEventRequest.PhoneVerificationProofToken"/>.
/// Returned exactly once - nothing re-derives it later.</param>
public sealed record ConfirmedPhoneVerificationResponse(
    Guid PendingPhoneVerificationId, string ProofToken, DateTimeOffset ProofExpiresAt);
