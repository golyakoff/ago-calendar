using System.Security.Cryptography;
using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG.
/// <see cref="RandomNumberGenerator.GetInt32(int, int)"/> rather than a hand-rolled modulo over
/// <see cref="RandomNumberGenerator.GetBytes(int)"/>, the same unbiased-draw call `ago-chat`'s own
/// <c>PendingChannelLinkCodeGenerator</c> makes for the identical reason.
/// </summary>
public sealed class PhoneVerificationCodeGenerator : IPhoneVerificationCodeGenerator
{
    // Six digits: 10^6 possibilities, small enough to type from memory within a few minutes, large
    // enough that guessing one within the short validity window is not a realistic attack on its own -
    // see IPhoneVerificationCodeGenerator's own remarks on why the real bound is scope and expiry, not
    // code length.
    private const int Digits = 6;
    private const int UpperBoundExclusive = 1_000_000;

    public string NewCode() =>
        RandomNumberGenerator.GetInt32(0, UpperBoundExclusive).ToString($"D{Digits}");
}
