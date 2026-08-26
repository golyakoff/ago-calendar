using System.Text.RegularExpressions;

namespace Ago.Calendar.Domain;

/// <summary>
/// The value a shop writes into its own page's <c>&lt;script&gt;</c> tag to say which tenant an embed
/// belongs to. Structurally what <c>Ago.Chat.Domain.Site.PublicKey</c> is, and deliberately not that
/// value: the two products own separate databases (adr/0027), so a shop running both holds two keys.
///
/// <para><b>Public, and not a secret - which is the whole reason the origin check exists.</b> This
/// string is readable by anyone who views the source of any page that embeds the booking surface, so
/// nothing may be authorised by knowing it. What it does is *name* a tenant early enough for a
/// browser's CORS preflight to be answered, which is exactly the timing problem `5-01` found live:
/// a preflight carries the URL and the <c>Origin</c> header and nothing else, so a tenant that can
/// only be identified from a request body cannot be identified during a preflight at all. Every
/// public read this product serves therefore carries the key <b>in the path</b>, never in the body.
/// </para>
///
/// <para><b>Chosen, not generated.</b> There is no randomness here and no platform port for any: the
/// key is supplied when the tenant is registered, the same way AGO Chat's own <c>demo_site</c> is a
/// value a seed script picked. Generating one would imply it were unguessable, and an unguessable
/// value nobody may rely on is a false promise with a random-number generator attached.</para>
/// </summary>
public readonly partial record struct TenantPublicKey
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    public string Value { get; }

    public TenantPublicKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length is < MinLength or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value.Length,
                $"A tenant public key is {MinLength}..{MaxLength} characters.");
        }

        // Lowercase only, so that "Barbershop" and "barbershop" can never be two tenants whose keys
        // differ by a shift key nobody can see in a script tag. The shape is also URL-safe by
        // construction, because the key travels in a path segment.
        if (!Allowed().IsMatch(value))
        {
            throw new ArgumentException(
                "A tenant public key is lowercase letters, digits, '_' and '-' only.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex Allowed();
}
