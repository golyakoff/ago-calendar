using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <param name="CalendarId">From the route - resolved to a tenant and cross-checked against the
/// verification row's own <c>TenantId</c>, the same cross-tenant collapse
/// <see cref="PhoneVerificationErrors.NotFound"/>'s own remarks describe.</param>
/// <param name="PendingPhoneVerificationId">From the route - the row <see cref="InitiatePhoneVerification"/>
/// created.</param>
/// <param name="Code">As typed by the visitor.</param>
/// <param name="Origin">The request's own <c>Origin</c> header, or null - `20-06`'s layer-2 check.</param>
public readonly record struct ConfirmPhoneVerification(
    CalendarId CalendarId, Guid PendingPhoneVerificationId, string Code, string? Origin);

/// <param name="ProofToken">The plaintext bearer proof - returned exactly once, here, and never again;
/// only its hash is ever stored (<c>PendingPhoneVerification.ProofTokenHash</c>). The widget carries it
/// unchanged into <c>BookEventRequest.PhoneVerificationProofToken</c>.</param>
public readonly record struct ConfirmedPhoneVerification(
    Guid PendingPhoneVerificationId, string ProofToken, DateTimeOffset ProofExpiresAt);
