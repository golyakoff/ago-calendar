using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <summary>
/// `20-10`: bound from `PhoneVerification:*` config keys - mirrors `ago-chat`'s own
/// <c>PhoneVerificationOptions</c> (`14-15`) shape and defaults, shared by
/// <c>InitiatePhoneVerificationHandler</c> (which reads every field) and
/// <c>ConfirmPhoneVerificationHandler</c> (which reads only <see cref="ProofValidFor"/> - the code's
/// own limits are already carried on a previously issued row, <c>PendingPhoneVerification.MaxAttempts</c>).
/// </summary>
public sealed class PhoneVerificationOptions
{
    public const string SectionName = "PhoneVerification";

    /// <summary>10 minutes, the identical default `14-15`'s own options class carries and for the
    /// identical reason: this code is read off an SMS or an incoming call the visitor is looking at
    /// right now, so there is little reason to hold the window open longer. Not measured or
    /// load-tested - the same honestly-stated-default caveat every <c>*Options</c> class in this
    /// codebase carries.</summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>5, the identical default `14-15` uses and for the identical reason: generous enough
    /// that two honest typos do not lock a visitor out, bounded enough that a script cannot realistically
    /// guess a six-digit code within <see cref="ValidFor"/>.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>SMS by default - the identical reasoning `14-15`'s own options class gives: a person
    /// reliably notices an SMS arriving, and a one-time code sitting unread past its short window is a
    /// failed verification.</summary>
    public PhoneVerificationDeliveryMethod DefaultDeliveryMethod { get; set; } = PhoneVerificationDeliveryMethod.Sms;

    /// <summary>
    /// 20 minutes: how long a confirmed proof token stays presentable to <c>POST .../book</c> before it
    /// must be re-verified. Longer than <see cref="ValidFor"/> deliberately - a visitor who has just
    /// proven their phone still has to pick a slot and fill in the rest of the booking form, which is a
    /// slower, more deliberate action than typing back a code that just arrived, but the window is not
    /// unbounded: an opaque bearer value with no expiry at all would be a permanent credential the
    /// moment it leaked from a browser's own network tab. Not measured - a starting point, the same
    /// caveat every duration in this file carries.
    /// </summary>
    public TimeSpan ProofValidFor { get; set; } = TimeSpan.FromMinutes(20);
}
