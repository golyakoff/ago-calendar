using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `20-10`: the write side of <see cref="PendingPhoneVerification"/> - shaped by its two real callers
/// (<c>InitiatePhoneVerificationHandler</c>, <c>ConfirmPhoneVerificationHandler</c>) and its one read-only
/// caller (<c>PhoneVerificationAssertionResolver</c>, from <c>BookEventHandler</c>'s own claim path),
/// never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md). Mirrors <c>ago-chat</c>'s own
/// <c>IPendingPhoneVerificationRepository</c> (`14-15`) in shape, not by reference.
/// </summary>
public interface IPendingPhoneVerificationRepository
{
    /// <summary>By primary key - the only lookup this item needs. Unfiltered by expiry/consumption/
    /// lockout, deliberately: both the confirm handler and the booking-time proof check need to see the
    /// row's real current state, which a "only ever returns a live row" query would hide.</summary>
    Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken);

    /// <summary>Adds a new row, or persists a mutation (<see cref="PendingPhoneVerification.AttemptConfirm"/>/
    /// <see cref="PendingPhoneVerification.IssueProof"/>) to one already tracked - the same "insert if
    /// new, update if not" shape folded into one method, since there is only ever one commit point per
    /// call.</summary>
    Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken);
}
