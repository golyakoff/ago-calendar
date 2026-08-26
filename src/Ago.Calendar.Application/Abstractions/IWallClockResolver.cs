using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// <b>The one place in AGO Calendar where a wall clock becomes an instant.</b> adr/0049 names four
/// notions of time and keeps them apart; this port is the single bridge between two of them, and
/// everything downstream of it works in absolute time only.
///
/// <para><b>Why it is a port at all, rather than a static helper.</b> Resolving
/// <c>Europe/Moscow</c> into real offsets means reading the tz database - ambient machine state,
/// updated out of band, different on a Windows dev box and a Linux container.
/// <see cref="CalendarTimeZone"/> deliberately validates only the *shape* of a zone id for exactly
/// that reason, and CLAUDE.md rule 2 puts every external resource behind a port. The alternative -
/// calling <c>TimeZoneInfo.FindSystemTimeZoneById</c> wherever a conversion is needed - is what
/// makes a time bug in a booking product unfindable: there is no single place to look, and no single
/// place to fix.</para>
///
/// <para><b>Why the API resolves a window rather than a single wall-clock time.</b> A point-valued
/// <c>ToInstant(date, time)</c> has to answer two questions a caller cannot see it answering: which
/// of the two instants an ambiguous local time (the hour that repeats when clocks fall back) means,
/// and what a local time that never happened (the hour skipped when they spring forward) resolves
/// to. Those answers differ for the opening and the closing edge of a working day - a shop opens at
/// the *first* time the clock reads 01:30 and closes at the *last* - so a single method would
/// silently pick one and be wrong at the other edge twice a year. Handing the whole window over
/// makes the asymmetry the resolver's own business and impossible for a caller to get wrong.</para>
/// </summary>
public interface IWallClockResolver
{
    /// <summary>
    /// The stretch of absolute time a business-local wall-clock window covers on one local day.
    ///
    /// <para>Returns <c>null</c> when a DST gap leaves no real time between the two edges - a rule
    /// reading 02:30-03:00 on the morning a zone jumps from 02:00 to 03:00 opens at a wall-clock
    /// time that never happened and closes at the instant of the jump itself, so it describes
    /// nothing. Null rather than an exception, because a zone changing its rules is not a caller's
    /// mistake; it is a day with no working time in it, which the materialiser handles the same way
    /// it handles a worker with no rule for a Sunday. (A window with *both* edges inside the gap is
    /// the other case and is not null: both move across it together and it keeps its length.)</para>
    /// </summary>
    /// <param name="zone">The owning calendar's IANA zone.</param>
    /// <param name="localDate">The business-local day, as it will be stored in
    /// <see cref="Event.LocalDate"/>.</param>
    /// <param name="opensAt">Wall-clock start, from a <see cref="WorkingHoursRule"/> or a tenant's
    /// manual edit.</param>
    /// <param name="closesAt">Wall-clock end, strictly after <paramref name="opensAt"/>.</param>
    /// <exception cref="UnknownCalendarTimeZoneException">The host's tz database has no such
    /// zone.</exception>
    TimeSlot? ToInstantWindow(CalendarTimeZone zone, DateOnly localDate, TimeOnly opensAt, TimeOnly closesAt);

    /// <summary>
    /// Which business-local day an instant falls on - the value stored in
    /// <see cref="Event.LocalDate"/>, and the materialiser's answer to "what is today for this
    /// calendar". Never the UTC date: 21:00 in New York is already tomorrow in UTC, and a horizon
    /// counted in UTC days would start a day late or early for half the world.
    /// </summary>
    /// <exception cref="UnknownCalendarTimeZoneException">The host's tz database has no such
    /// zone.</exception>
    DateOnly ToLocalDate(CalendarTimeZone zone, DateTimeOffset instant);
}
