using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Application.Tests;

/// <summary>Hand-written fakes for `20-10`'s own two use cases - the same "a field holding the calls"
/// discipline <see cref="BookingFakes"/>'s own file-level remarks already state for this test
/// project.</summary>
internal sealed class FixedPhoneVerificationCodeGenerator(string code = "482913") : IPhoneVerificationCodeGenerator
{
    public string NewCode() => code;
}

internal sealed class FixedPhoneVerificationProofTokenGenerator(string token = "proof-token-abc")
    : IPhoneVerificationProofTokenGenerator
{
    public string NewToken() => token;
}

/// <summary>Records every send it was asked to make - the assertion surface for "the code was
/// actually sent, with the right phone and method" and, just as importantly, for "nothing was sent"
/// on a rejected attempt.</summary>
internal sealed class FakePhoneVerificationSender : IPhoneVerificationSender
{
    public List<PhoneVerificationDelivery> Sent { get; } = [];

    public Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken)
    {
        Sent.Add(delivery);
        return Task.CompletedTask;
    }
}
