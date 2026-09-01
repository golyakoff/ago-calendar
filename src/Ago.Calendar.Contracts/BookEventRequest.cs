namespace Ago.Calendar.Contracts;

/// <summary>
/// The booking request's body. The calendar and the event come from the route, not from here - a
/// resource's identity belongs in its URL (api-design.md), and duplicating them in the body would
/// create a second, disagreeing copy somebody has to decide between.
/// </summary>
/// <param name="ServiceId">What the customer is booking. Exactly one in v1.</param>
/// <param name="Phone">As typed. Normalised server-side, so a customer may write it any way they
/// like and still reach the same lead card.</param>
/// <param name="DisplayName">Optional. Never overwrites a name an operator already curated.</param>
/// <param name="PhoneVerificationId">
/// `20-10`: the <c>pendingPhoneVerificationId</c> a prior
/// <c>POST .../phone-verifications/{id}/confirm</c> call returned. Null for a returning customer whose
/// phone is already verified from an earlier booking - <see cref="Ago.Calendar.Application.UseCases.BookEvent.BookEventHandler"/>'s
/// own <c>PhoneVerificationAssertionResolver</c> checks that shortcut first and only needs this field
/// when it comes back empty.
/// </param>
/// <param name="PhoneVerificationProofToken">
/// `20-10`: the plaintext bearer proof the same confirm call returned, paired with
/// <paramref name="PhoneVerificationId"/> - unforgeable and bound to the exact phone number it was
/// issued for (<c>PendingPhoneVerification.IsProofValid</c>).
/// </param>
public sealed record BookEventRequest(
    Guid ServiceId,
    string Phone,
    string? DisplayName,
    Guid? PhoneVerificationId = null,
    string? PhoneVerificationProofToken = null);
