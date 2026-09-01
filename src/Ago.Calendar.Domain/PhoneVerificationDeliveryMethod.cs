namespace Ago.Calendar.Domain;

/// <summary>`20-10`: which channel a code was sent on - mirrors <c>ago-chat</c>'s own
/// <c>Ago.Chat.Domain.PhoneVerificationDeliveryMethod</c> (`14-15`) in shape, not by reference
/// (`adr/0027`). Voice is included for the same fidelity reason `14-15` carries it even though this
/// item's own <c>FakePhoneVerificationSender</c> treats both identically - the swap point for a real
/// vendor later needs somewhere to record which one a tenant asked for.</summary>
public enum PhoneVerificationDeliveryMethod
{
    Sms,
    Call,
}
