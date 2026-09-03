using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`22-11`: the implementation of <see cref="IModuleProvisioningAuthenticator"/> - see that
/// interface's own remarks for why this is a constant-time raw-secret compare rather than
/// <c>HmacModuleCallCredentialValidator</c>'s signed-assertion scheme.</summary>
public sealed class SharedSecretModuleProvisioningAuthenticator(IOptions<ModuleProvisioningOptions> options)
    : IModuleProvisioningAuthenticator
{
    public bool Authenticate(string? headerValue)
    {
        var configured = options.Value.Secret;

        // An empty configured secret never authenticates anything, regardless of what is presented -
        // see ModuleProvisioningOptions.Secret's own remarks. Checked before the presented value so a
        // misconfigured deployment fails closed rather than accidentally accepting an empty header.
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        var presentedBytes = Encoding.UTF8.GetBytes(headerValue);

        // FixedTimeEquals throws ArgumentException for mismatched lengths rather than comparing them
        // in constant time, so the length check has to happen first regardless - the same guard
        // HmacModuleCallCredentialValidator's own signature comparison does not need only because an
        // HMAC output is always a fixed 32 bytes. The length itself is not the secret - an attacker
        // who already sees this header's wire size learns nothing new from a fast length-mismatch
        // response that the HTTP framing had not already told them.
        return configuredBytes.Length == presentedBytes.Length
            && CryptographicOperations.FixedTimeEquals(configuredBytes, presentedBytes);
    }
}
