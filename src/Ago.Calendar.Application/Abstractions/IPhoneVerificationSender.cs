using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `20-10`: the outbound half of phone verification - given a plaintext code and a phone number, places
/// the real SMS or voice call. Deliberately provider-neutral, mirroring <c>ago-chat</c>'s own
/// <c>IPhoneVerificationSender</c> (`14-15`): no gateway's own request/response shape, auth scheme, or
/// vendor name may appear on this interface or anywhere in <c>Ago.Calendar.Application</c>.
///
/// <para><b>The one implementation this item ships with is <c>FakePhoneVerificationSender</c>
/// (<c>Ago.Calendar.Module.PhoneVerification</c>), registered unconditionally - not
/// `ago-chat`'s own <c>UnconfiguredPhoneVerificationSender</c>-shaped "throw, this is not configured."</b>
/// See that type's own remarks for why: this item's whole point is a real, live, first-time visitor
/// completing a real booking through the public widget, which a sender that refuses every call could
/// never demonstrate. The swap point for a real vendor, later, is unchanged from `14-15`'s own
/// precedent: a concrete <c>Ago.Calendar.Infrastructure.&lt;Vendor&gt;.&lt;Vendor&gt;PhoneVerificationSenderClient</c>
/// implements this identical port and replaces the DI registration - nothing in <c>Application</c>/
/// <c>Domain</c> moves.</para>
///
/// <para><b>Called synchronously, inline, from <c>InitiatePhoneVerificationHandler</c> - a deliberate
/// divergence from `14-15`'s own out-of-band, outbox-driven <c>Worker</c> dispatch, named here rather
/// than silently copied or silently dropped.</b> `14-15` routes the actual send through
/// <c>Ago.Chat.Worker</c>'s own consumer because a real SMS/voice gateway call costs real money per
/// attempt and can hang, and CLAUDE.md rule 4's "never publish from inside a request handler" is written
/// with exactly that cost in mind. Neither risk applies to <c>FakePhoneVerificationSender</c>: it is a
/// structured log write, and it cannot hang or spend budget. Building the outbox-plus-consumer relay this
/// item's own fake sender would never need - AGO Calendar has no messaging infrastructure at all today,
/// no <c>IOutboxWriter</c> caller anywhere in this product - would be exactly the premature
/// generalisation clean-architecture.md warns against, for a cost this port's only real implementation
/// does not have. The day a real, money-spending vendor client replaces the fake sender is the day this
/// call site needs to move off the request thread, and that day's own item should build the relay against
/// a port that then genuinely needs it, not before.</para>
///
/// <para><b>Throws, does not return a result type</b>, the same reasoning `14-15`'s own port gives: there
/// is no caller-visible "refused but not thrown" state for this handler to record, so a transient failure
/// is an ordinary exception and a terminal, retry-proof refusal is a genuine fault. Both simply propagate
/// to the endpoint as a 500 today - there is no resilience pipeline wrapping this port yet, because
/// nothing has ever needed one: the fake sender never throws.</para>
/// </summary>
public interface IPhoneVerificationSender
{
    Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>The only thing a sender needs to know: a gateway call is "text/call this number with this
/// code," nothing else - a port shaped around anything wider would leak this item's own persistence
/// concerns into Infrastructure.</summary>
public sealed record PhoneVerificationDelivery(string Phone, string Code, PhoneVerificationDeliveryMethod Method);
