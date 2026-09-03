using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-02`: the receiving half of `Ago.Chat.Infrastructure.Modules.ModuleCallCredential` - the same
/// wire shape, hand-kept in sync the way every other cross-repository contract in this project is (no
/// shared package between `ago-chat` and `ago-calendar`, adr/0012 sets no precedent for one). If you are
/// changing this file, the identical change belongs in `ago-faq`'s own
/// <c>HmacModuleCallCredentialValidator</c> too - see this item's own report for the exact format both
/// sides agree on.
///
/// <list type="bullet">
/// <item>Header: <c>X-Ago-Module-Credential</c>.</item>
/// <item>Token: <c>{base64url(payload JSON)}.{base64url(HMAC-SHA256(secret, UTF8(that base64url
/// string)))}</c> - the signature covers the transmitted first segment's own bytes, never a
/// re-serialization, so there is nothing about JSON whitespace or property order for the two sides to
/// keep in sync.</item>
/// <item>Payload: <c>{"siteId":"&lt;guid&gt;","iat":&lt;unix seconds&gt;,"exp":&lt;unix seconds&gt;}</c>.</item>
/// </list>
///
/// <para><b>Constant-time comparison</b> (<see cref="CryptographicOperations.FixedTimeEquals"/>) - a
/// validator that returns "invalid" faster for a signature that differs in its first byte than one that
/// differs in its last leaks the correct signature one byte at a time to a patient attacker; this is the
/// one place in this file that distinction actually matters, so it is not left to the default
/// <c>SequenceEqual</c>-shaped comparison an inline check would probably reach for.</para>
///
/// <para><b>A five-second clock-skew allowance</b> on <c>exp</c>, in both directions - the same
/// tolerance a `20-10` phone-verification-code TTL uses for the identical reason: two independent hosts'
/// clocks are never perfectly synchronized, and `docs/conventions/date-and-time.md`'s own UTC discipline
/// does not by itself guarantee NTP agreement.</para>
/// </summary>
public sealed class HmacModuleCallCredentialValidator(ModuleCallCredentialOptions options)
    : IModuleCallCredentialValidator
{
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ModuleCallCredentialResult Validate(string? headerValue, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            // `22-02`'s own rollout affordance: while this deployment has not yet made a credential
            // mandatory, an absent header is treated as pre-migration traffic, not a refusal. There is
            // nothing to cross-check a site id against in that case (SiteId stays null) - see the
            // interface's own remarks on why a caller must treat that as "skip the check", not "matched".
            return new ModuleCallCredentialResult(!options.RequireCredential, SiteId: null);
        }

        if (string.IsNullOrEmpty(options.SharedSecret))
        {
            // Configured to require a credential (or one was presented anyway) but this deployment
            // itself has no secret to check it against - a deployment fault, not a caller mistake, and
            // refusing is the only honest answer: silently accepting would mean nothing this deployment
            // ever answers is really checked.
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        var parts = headerValue.Split('.');
        if (parts.Length != 2)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        var encodedPayload = parts[0];
        byte[] presentedSignature;
        byte[] payloadBytes;
        try
        {
            presentedSignature = Base64Url.Decode(parts[1]);
            payloadBytes = Base64Url.Decode(encodedPayload);
        }
        catch (FormatException)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        var expectedSignature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.SharedSecret), Encoding.UTF8.GetBytes(encodedPayload));
        if (!CryptographicOperations.FixedTimeEquals(presentedSignature, expectedSignature))
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        if (payload is null)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        var nowSeconds = now.ToUnixTimeSeconds();
        var skewSeconds = (long)ClockSkewAllowance.TotalSeconds;
        if (nowSeconds > payload.Exp + skewSeconds || nowSeconds < payload.Iat - skewSeconds)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        return new ModuleCallCredentialResult(IsAuthenticated: true, payload.SiteId);
    }

    private sealed record Payload(
        [property: JsonPropertyName("siteId")] Guid SiteId,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}

/// <summary>RFC 4648 §5 base64url, without padding - the identical four-line hand-rolled helper
/// `Ago.Chat.Infrastructure.Modules.Base64Url` uses on the minting side (no shared package between the
/// two repositories to put a single copy in, and .NET's own <see cref="Convert"/> only offers the
/// standard alphabet).</summary>
internal static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (padded.Length % 4)) % 4;
        return Convert.FromBase64String(padded + new string('=', padding));
    }
}
