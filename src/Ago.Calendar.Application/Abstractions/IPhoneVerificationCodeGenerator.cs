namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `20-10`: the plaintext confirmation code sent to a visitor, mirroring `ago-chat`'s own
/// <c>IPendingChannelLinkCodeGenerator</c>/<c>IPhoneVerificationOptions</c> defaults in shape.
///
/// <para>Deliberately low entropy, unlike <see cref="IPhoneVerificationProofTokenGenerator"/>: this
/// value is read off an SMS or a phone call and typed back by hand within a few minutes, so it has to
/// be short enough that this is not itself the obstacle. The resulting brute-force surface is bounded
/// by <c>PendingPhoneVerification.MaxAttempts</c> and its short <c>ExpiresAt</c> window, not by code
/// length - see that type's own remarks.</para>
/// </summary>
public interface IPhoneVerificationCodeGenerator
{
    /// <summary>A short, numeric, human-typeable code - never a UUID or anything with a fixed
    /// structure that reads as more secret than it is.</summary>
    string NewCode();
}
