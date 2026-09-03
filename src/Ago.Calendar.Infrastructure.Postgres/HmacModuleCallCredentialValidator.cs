using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-02`/`22-04`: the receiving half of `Ago.Chat.Infrastructure.Modules.ModuleCallCredential` - the
/// same wire shape, hand-kept in sync the way every other cross-repository contract in this project is
/// (no shared package between `ago-chat` and `ago-calendar`, adr/0012 sets no precedent for one). If
/// you are changing this file, the identical change belongs in `ago-faq`'s own
/// <c>HmacModuleCallCredentialValidator</c> too.
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
/// <para><b>`22-04`: the secret is per tenant, not per deployment.</b> Before this item, one configured
/// <c>ChatModule:SharedSecret</c> verified every call this deployment ever received, regardless of
/// which site it claimed - adr/0094's own named limit ("whoever holds the raw secret can mint one for
/// any site that deployment serves"). This class now reads the payload's claimed site id <b>before</b>
/// it can know which secret to check the signature against - the payload itself is not trusted yet at
/// that point, only used as a lookup key (the claimed id is treated as a <see cref="TenantId"/>, per
/// <see cref="ChatModuleRegistration"/>'s own remarks on why the two are the same value) - and only a
/// signature that verifies against that exact tenant's own
/// <see cref="ChatModuleRegistration.Credential"/> is ever accepted. A token forged for tenant A by
/// copying a genuine one and editing the site id to B fails here: the signature was computed with A's
/// secret, and B's row (if one exists at all) holds a different one.</para>
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
public sealed class HmacModuleCallCredentialValidator(IChatModuleRegistrationRepository registrations)
    : IModuleCallCredentialValidator
{
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ModuleCallCredentialResult> ValidateAsync(
        string? headerValue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // `22-04`: no accepting-but-warning window left. Per-tenant resolution has no
        // deployment-wide tenant to fall back to, so a call with nothing to authenticate has nothing
        // to resolve into - see IModuleCallCredentialValidator's own remarks.
        if (string.IsNullOrEmpty(headerValue))
        {
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

        // The claimed site id, not yet trusted - only used to find which secret this signature must
        // verify against. A tenant with no registration here has no secret to check anything
        // against, which is exactly "the chat module is not enabled for this site": refused, not a
        // deployment fault.
        var registration = await registrations.GetByTenantIdAsync(new TenantId(payload.SiteId), cancellationToken);
        if (registration is null)
        {
            return new ModuleCallCredentialResult(IsAuthenticated: false, SiteId: null);
        }

        var expectedSignature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(registration.Credential.Value), Encoding.UTF8.GetBytes(encodedPayload));
        if (!CryptographicOperations.FixedTimeEquals(presentedSignature, expectedSignature))
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
