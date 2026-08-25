namespace Ago.Calendar.Domain;

/// <summary>
/// The IANA zone a calendar's *wall clock* is read in - <c>Europe/Moscow</c>, never <c>+03:00</c>.
/// date-and-time.md states the reason in one line ("offsets do not survive DST"); a booking product
/// is where that stops being a style rule. A shop whose rule says 09:00-18:00 opens at a different
/// UTC instant in summer than in winter in any zone that observes DST, and an offset stored once
/// cannot express that.
///
/// <para><b>Shape is checked here; existence is not.</b> This type rejects an empty value and
/// rejects anything that is plainly an offset rather than a zone id. It deliberately does not call
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c>: the tz database is an ambient, machine-dependent
/// resource, updated out of band and different between a Windows dev box and a Linux container -
/// exactly the class of thing CLAUDE.md rule 2 keeps out of Domain. Resolving this id into real
/// offsets is `20-02`'s materialiser, in Infrastructure, where a missing zone is an infrastructure
/// fault with an owner. The alternative - validating here - would make constructing a calendar
/// succeed or fail depending on which machine ran the code, and would make the Domain untestable
/// without the host's tz data.</para>
/// </summary>
public readonly record struct CalendarTimeZone
{
    public CalendarTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A calendar's time zone must be an IANA zone id, not empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed[0] is '+' or '-' || trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{value}' looks like a UTC offset. Store an IANA zone id (Europe/Moscow, for example) - " +
                "an offset does not survive a DST transition.",
                nameof(value));
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
