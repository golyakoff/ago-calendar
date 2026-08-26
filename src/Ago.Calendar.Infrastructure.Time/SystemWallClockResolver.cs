using System.Collections.Concurrent;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Time;

/// <summary>
/// The only type in AGO Calendar that touches <see cref="TimeZoneInfo"/>, and therefore the only
/// place a wall-clock reading becomes an instant (adr/0049). Everything above it - the
/// materialiser, the manual-edit handlers, <see cref="SlotGrid"/>, <see cref="Event"/> - works in
/// absolute time and never converts again.
///
/// <para><b>The two hard cases, resolved explicitly rather than by whatever the BCL happened to
/// do.</b> <c>TimeZoneInfo.GetUtcOffset</c> answers both of them, but it answers them silently and
/// its answer for an ambiguous time is the *second* occurrence - which is the wrong edge for an
/// opening time. Naming both cases here is what makes the behaviour reviewable:</para>
/// <list type="bullet">
///   <item><b>The skipped hour (spring forward).</b> 02:30 does not exist on the morning a zone
///   jumps 02:00 -> 03:00. A window edge landing there is moved forward to the first instant that
///   does exist, so a shop opening at 02:30 opens the moment the clock reaches 03:30. Shifting
///   *backwards* was the alternative and is worse: it would open the shop before the day it belongs
///   to, and on the closing edge it would make the day end before it started.</item>
///   <item><b>The repeated hour (fall back).</b> 01:30 happens twice. The opening edge takes the
///   first occurrence and the closing edge the last, so a working day that brackets the transition
///   is the longer of the two readings - the worker really is at work for both passes of that hour,
///   and any other choice silently deletes an hour of real availability.</item>
/// </list>
///
/// <para>Resolved zones are cached because <c>FindSystemTimeZoneById</c> reads the tz database on
/// every call and one materialisation run asks for the same zone once per day per worker. A
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a lock: the value is immutable and
/// the worst case of two threads racing is one redundant lookup, which is cheaper than the
/// contention a lock would add on the hot path (concurrency.md's own "do not synchronise what is
/// idempotent" note). Registered as a singleton so the cache actually survives between scopes.</para>
/// </summary>
public sealed class SystemWallClockResolver : IWallClockResolver
{
    private readonly ConcurrentDictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);

    public TimeSlot? ToInstantWindow(
        CalendarTimeZone zone, DateOnly localDate, TimeOnly opensAt, TimeOnly closesAt)
    {
        if (closesAt <= opensAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closesAt), closesAt,
                $"A working window must close after it opens; got {opensAt:HH\\:mm} .. {closesAt:HH\\:mm}. " +
                "A window crossing midnight is two windows, on two days (see WorkingHoursRule).");
        }

        var timeZone = Resolve(zone);
        var startsAt = ToInstant(timeZone, localDate.ToDateTime(opensAt, DateTimeKind.Unspecified), preferEarlier: true);
        var endsAt = ToInstant(timeZone, localDate.ToDateTime(closesAt, DateTimeKind.Unspecified), preferEarlier: false);

        // The two edges can resolve out of order: an opening time inside a DST gap is pushed across
        // it while a closing time just after the gap is not, so 02:30-03:00 ends before it starts.
        // That is a day with no working time, not an error - see IWallClockResolver.ToInstantWindow
        // for why null rather than a throw, and why a window with *both* edges in the gap is a
        // different case that keeps its length.
        return endsAt <= startsAt ? null : new TimeSlot(startsAt, endsAt);
    }

    public DateOnly ToLocalDate(CalendarTimeZone zone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Resolve(zone)).DateTime);

    /// <summary>
    /// Returns the instant <b>normalised to UTC</b>, not carrying the zone's own offset.
    ///
    /// <para>CLAUDE.md rule 11 and adr/0011: in memory an instant is always a UTC
    /// <see cref="DateTimeOffset"/>, and the local offset is a rendering parameter, not a value the
    /// domain carries around. Two representations of the same instant circulating - one from the
    /// resolver at <c>-05:00</c>, one read back from <c>timestamptz</c> at <c>+00:00</c> - would
    /// compare equal on <c>==</c> and unequal on <c>Equals</c>, which is the sort of ambiguity a
    /// time model exists to remove. Npgsql agrees, and refuses any other offset for a
    /// <c>timestamptz</c> parameter; normalising here rather than at the adapter means one rule
    /// rather than a rule plus a conversion everybody has to remember.</para>
    /// </summary>
    /// <param name="preferEarlier"><c>true</c> for an opening edge, <c>false</c> for a closing one -
    /// the asymmetry the repeated hour needs, and the reason this is private and the public API
    /// takes a whole window.</param>
    private static DateTimeOffset ToInstant(TimeZoneInfo timeZone, DateTime local, bool preferEarlier)
    {
        if (timeZone.IsAmbiguousTime(local))
        {
            // Two offsets are on offer. The larger offset is the earlier instant (still on summer
            // time), the smaller is the later one - so "earlier occurrence" is Max, which reads
            // backwards until you write down that UTC = local - offset.
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var chosen = preferEarlier ? offsets.Max() : offsets.Min();
            return new DateTimeOffset(local, chosen).ToUniversalTime();
        }

        // For a time inside a DST gap, GetUtcOffset returns the offset that was in force *before*
        // the transition, which lands the instant on the far side of the gap - 02:30 becomes 03:30.
        // That is the behaviour this resolver wants, and WallClockResolverTests asserts it directly
        // rather than trusting this remark: a BCL behaviour that only a comment claims is a
        // behaviour that changes under a refactor nobody notices.
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private TimeZoneInfo Resolve(CalendarTimeZone zone)
    {
        if (_zones.TryGetValue(zone.Value, out var cached))
        {
            return cached;
        }

        TimeZoneInfo resolved;
        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(zone.Value);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new UnknownCalendarTimeZoneException(zone, exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            // The entry exists but is corrupt - same class of deployment fault, same owner.
            throw new UnknownCalendarTimeZoneException(zone, exception);
        }

        return _zones.GetOrAdd(zone.Value, resolved);
    }
}
