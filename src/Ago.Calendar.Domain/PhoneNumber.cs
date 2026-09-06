using System.Text;

namespace Ago.Calendar.Domain;

/// <summary>
/// The customer's only mandatory identifier (the product spec: no account, no password, a lead card
/// keyed by phone). Normalised on construction, so that <c>+7 (999) 123-45-67</c> and
/// <c>+79991234567</c> are one customer rather than two - which matters because the storage-level
/// uniqueness this backs (<c>ux_customers_tenant_phone</c>) compares bytes, not intent.
///
/// <para><b>Validated for shape, never for reachability.</b> E.164 shape - a leading <c>+</c>, no
/// leading zero, 8 to 15 digits - is a rule this type can decide alone. Whether a number rings is a
/// fact about the world that only an SMS gateway knows (`20-05`), so it is not a domain invariant,
/// and pretending otherwise would put an outbound network call behind a constructor.</para>
///
/// <para>Personal data: this is the one field in <c>Ago.Calendar.Domain</c> that directly identifies
/// a natural person - see <c>ago-root/docs/architecture/personal-data.md</c>.</para>
/// </summary>
public readonly record struct PhoneNumber
{
    private const int MinDigits = 8;
    private const int MaxDigits = 15;

    public PhoneNumber(string value)
    {
        Value = Normalise(value);
    }

    public string Value { get; }

    public override string ToString() => Value;

    /// <summary>
    /// `23-12`/`decisions.md` §5: the masked display form a caller sees under
    /// <see cref="ContactVisibility.MaskedWithReveal"/> - keeps the first two and last two characters
    /// (enough that an operator can sanity-check "this looks like a real number") and replaces
    /// everything between with a fixed run of bullets. The identical algorithm `ago-chat`'s own
    /// <c>ListVisitorContactDetailsHandler.Mask</c> uses for the same purpose over its own store -
    /// independently implemented rather than shared, since <c>Ago.Chat.*</c> is not a project this one
    /// may reference (adr/0027) - restated here on <see cref="PhoneNumber"/> itself, rather than
    /// duplicated across every read store that needs it, because it is a pure function of the value
    /// this type already holds and every read store that masks a phone can already reference
    /// <c>Ago.Calendar.Domain</c>.
    ///
    /// <para>The short-value branch below is unreachable for any <see cref="PhoneNumber"/> this type
    /// can construct (<see cref="MinDigits"/> plus the leading <c>+</c> is already nine characters),
    /// kept anyway so this method matches its chat-side twin exactly rather than silently depending on
    /// today's minimum length.</para>
    /// </summary>
    public string Masked() =>
        Value.Length <= 4
            ? new string('•', Value.Length)
            : $"{Value[..2]}{new string('•', Value.Length - 4)}{Value[^2..]}";

    private static string Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var digits = new StringBuilder(MaxDigits);
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
                continue;
            }

            if (!IsIgnorableSeparator(character))
            {
                throw new ArgumentException(
                    $"'{value}' is not a phone number: unexpected character '{character}'.", nameof(value));
            }
        }

        if (digits.Length is < MinDigits or > MaxDigits)
        {
            throw new ArgumentException(
                $"A phone number must carry {MinDigits}-{MaxDigits} digits; '{value}' carries {digits.Length}.",
                nameof(value));
        }

        if (digits[0] == '0')
        {
            throw new ArgumentException(
                $"A phone number must be in international form (country code first); '{value}' starts with 0.",
                nameof(value));
        }

        return string.Concat("+", digits);
    }

    private static bool IsIgnorableSeparator(char character) =>
        character is ' ' or '-' or '.' or '+' or '(' or ')';
}
