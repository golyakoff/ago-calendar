namespace Ago.Calendar.Domain;

/// <summary>
/// `20-10`: the answer <see cref="PendingPhoneVerification.AttemptConfirm"/> hands back, one member per
/// Done-when case this item's own backlog names ("a wrong code is refused, a code is locked out after
/// too many wrong attempts, and an expired code is refused"). Mirrors <c>ago-chat</c>'s own
/// <c>Ago.Chat.Domain.PhoneVerificationConfirmOutcome</c> (`14-15`) in shape - the identical five
/// outcomes a real visitor routinely hits - without referencing that assembly: `adr/0027` keeps AGO
/// Calendar independently deployable, so this is this repository's own, second copy of the same idea
/// rather than a shared dependency.
/// </summary>
public enum PhoneVerificationConfirmOutcome
{
    Confirmed,
    WrongCode,
    Expired,
    LockedOut,
    AlreadyConsumed,
}
