namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `20-10`: the bearer proof <c>ConfirmPhoneVerificationHandler</c> mints on a confirmed verification -
/// what <c>BookEventRequest</c> later carries to prove the phone was actually verified, unforgeably.
///
/// <para>High entropy, unlike <see cref="IPhoneVerificationCodeGenerator"/>'s own six digits: this
/// value is never typed by a person, only echoed back verbatim by the widget's own JavaScript, so
/// there is no human-typeable-length constraint working against making it genuinely unguessable - the
/// same "opaque bearer secret" shape <c>ago-chat</c>'s own <c>IWebhookSecretGenerator</c> already
/// establishes for this codebase's other bearer credential.</para>
/// </summary>
public interface IPhoneVerificationProofTokenGenerator
{
    /// <summary>A high-entropy value, never a UUID or anything else with a fixed, guessable
    /// structure.</summary>
    string NewToken();
}
