using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// A calendar's stored IANA zone id is not in the host's tz database.
///
/// <para><b>An infrastructure fault with an owner, not a validation failure.</b>
/// <see cref="CalendarTimeZone"/> already refused anything offset-shaped when the calendar was
/// created, so a zone id reaching this point was well-formed and was accepted by some machine at
/// some point. Failing here means the *machine* is wrong - a container image without tzdata, or a
/// zone the IANA project has since retired - and the fix is a deployment fix. Translated by the
/// adapter so no handler ever sees <c>TimeZoneNotFoundException</c> and mistakes it for something a
/// user typed.</para>
/// </summary>
public sealed class UnknownCalendarTimeZoneException(CalendarTimeZone zone, Exception innerException)
    : Exception(
        $"The host's time zone database has no entry for '{zone.Value}'. " +
        "The calendar cannot be materialised until the host can resolve its zone.",
        innerException)
{
    public CalendarTimeZone Zone { get; } = zone;
}
