using System.Security.Cryptography;
using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG, 256 bits
/// of entropy base64url-encoded, the identical primitive and shape `ago-chat`'s own
/// <c>WebhookSecretGenerator</c> uses for its own opaque bearer credential.
/// </summary>
public sealed class PhoneVerificationProofTokenGenerator : IPhoneVerificationProofTokenGenerator
{
    private const string Prefix = "phvfy_";
    private const int SecretBytesLength = 32;

    public string NewToken() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytesLength))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
