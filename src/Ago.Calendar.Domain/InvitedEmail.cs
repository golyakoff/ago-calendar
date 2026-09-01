namespace Ago.Calendar.Domain;

/// <summary>
/// The address a tenant types on the Access screen's "invite a colleague" form (adr/0088) - what
/// <c>OperatorIdentityClaimsTransformation</c>'s email fallback later matches a first sign-in
/// against. Normalised on construction (trimmed, lower-cased) so <c>Alex@Shop.example</c> and
/// <c>alex@shop.example</c> are the same match - the same reasoning <see cref="PhoneNumber"/>'s own
/// remarks give for normalising a customer's number, applied to the field this product's own
/// <c>operators</c> table now carries.
///
/// <para><b>Validated for shape, never for reachability</b> - the same split <see cref="PhoneNumber"/>
/// draws. One <c>@</c>, a non-empty part on each side, is all this type can decide on its own; whether
/// the address is real is a fact only a real sign-in attempt can prove, and adr/0088's own consequence
/// section names that honestly (a mistyped invite silently never links) rather than papering over it
/// with a cleverer match.</para>
///
/// <para><b>Deliberately not unique at the storage level.</b> Two invited rows - in the same tenant or
/// different ones - may share an address; adr/0088's own Done-when asks for that collision to be
/// handled by the fallback refusing to guess, not by a constraint that makes the case impossible to
/// reach. See <c>IOperatorRepository.FindInvitedByEmailAsync</c>.</para>
///
/// <para>Personal data: a direct identifier, where <c>operators</c> previously held only a display
/// name - see <c>ago-root/docs/architecture/personal-data.md</c> (that file's own row needs updating
/// alongside this type, in the repository that owns it).</para>
/// </summary>
public readonly record struct InvitedEmail
{
    public InvitedEmail(string value)
    {
        Value = Normalise(value);
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static string Normalise(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@');

        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            throw new ArgumentException($"'{value}' is not an email address.", nameof(value));
        }

        return trimmed.ToLowerInvariant();
    }
}
