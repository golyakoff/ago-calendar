using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Time;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The single wall-clock-to-instant bridge, tested against the host's real tz database.
///
/// <para><b>An integration test, and not by accident.</b> The tz database is exactly the kind of
/// ambient external resource that put <see cref="SystemWallClockResolver"/> in an Infrastructure
/// project in the first place - it is updated out of band and differs between a Windows dev box and
/// a Linux container. A fake of it would prove that the fake behaves as its author expected, which
/// is the thing testing.md says a mocked dependency proves. No Postgres is involved, so these do not
/// take the collection fixture.</para>
///
/// <para><c>America/New_York</c> throughout: Russia has not observed DST since 2014, so every
/// assertion below would pass in <c>Europe/Moscow</c> against code that stored a fixed offset.</para>
/// </summary>
public class WallClockResolverTests
{
    private static readonly CalendarTimeZone NewYork = new("America/New_York");
    private readonly IWallClockResolver _resolver = new SystemWallClockResolver();

    [Fact]
    public void TheSameWallClockWindow_ResolvesToDifferentInstants_EitherSideOfASpringForward()
    {
        var before = Window(new DateOnly(2026, 3, 7), 9, 17);
        var after = Window(new DateOnly(2026, 3, 14), 9, 17);

        // Same 09:00 on the wall, two different instants: 14:00Z on standard time, 13:00Z on
        // daylight time. The resolved values are UTC (CLAUDE.md rule 11) - the zone's own offset is
        // what produced the difference, not what is carried away from here.
        Assert.Equal(TimeSpan.Zero, before.StartsAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), before.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 14, 13, 0, 0, TimeSpan.Zero), after.StartsAt);

        // Seven days on the wall clock, six days and twenty-three hours of real time. This is the
        // difference an offset stored on the calendar would have erased.
        Assert.Equal(TimeSpan.FromDays(7) - TimeSpan.FromHours(1), after.StartsAt - before.StartsAt);

        // Both days are still eight hours long, because both edges moved together.
        Assert.Equal(TimeSpan.FromHours(8), before.Duration);
        Assert.Equal(TimeSpan.FromHours(8), after.Duration);
    }

    [Fact]
    public void AWindowBracketingASpringForward_IsAnHourShorterThanItReads()
    {
        // 2026-03-08: 02:00 becomes 03:00. Five hours on the wall, four in real time.
        var window = Window(new DateOnly(2026, 3, 8), 1, 6);

        Assert.Equal(TimeSpan.FromHours(4), window.Duration);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), window.StartsAt.ToUniversalTime());
    }

    [Fact]
    public void AnEdgeInsideTheSkippedHour_MovesForwardToTheFirstInstantThatExists()
    {
        // 02:30 does not happen on this date. Asserted rather than assumed: the resolver leans on
        // TimeZoneInfo.GetUtcOffset returning the pre-transition offset for an invalid local time,
        // and a BCL behaviour a comment merely claims is a behaviour that changes under a refactor
        // nobody notices.
        var window = _resolver.ToInstantWindow(NewYork, new DateOnly(2026, 3, 8), new TimeOnly(2, 30), new TimeOnly(6, 0));

        Assert.NotNull(window);

        // 02:30 nominal lands on 03:30 EDT = 07:30Z - forward across the gap, never backwards, which
        // would have opened the shop before the hour it was told to.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), window.Value.StartsAt.ToUniversalTime());
    }

    [Fact]
    public void AWindowWithBothEdgesInsideTheSkippedHour_KeepsItsLengthAndMovesWholesale()
    {
        // 02:15-02:45 on the morning the clocks jump. Neither edge happened, both are pushed across
        // the gap by the same hour, and the result is still half an hour - at 03:15-03:45 local.
        // Pinned rather than left to chance: it is the behaviour a shop would expect ("we were open
        // for half an hour"), and it is only true because both edges are resolved the same way.
        var window = _resolver.ToInstantWindow(NewYork, new DateOnly(2026, 3, 8), new TimeOnly(2, 15), new TimeOnly(2, 45));

        Assert.NotNull(window);
        Assert.Equal(TimeSpan.FromMinutes(30), window.Value.Duration);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 15, 0, TimeSpan.Zero), window.Value.StartsAt);
    }

    [Fact]
    public void AWindowThatStartsInsideTheSkippedHourAndEndsJustAfterIt_IsNoWindowAtAll()
    {
        // 02:30-03:00. The opening edge did not happen and is pushed forward to 03:30; the closing
        // edge did happen, at the very instant the clocks jumped. So the window resolves to a
        // negative span - not an error, just a stretch of wall clock with no real time in it. Null
        // rather than a throw: the materialiser treats it exactly like a day with no rule, which is
        // what it is.
        var window = _resolver.ToInstantWindow(NewYork, new DateOnly(2026, 3, 8), new TimeOnly(2, 30), new TimeOnly(3, 0));

        Assert.Null(window);
    }

    [Fact]
    public void AWindowBracketingAFallBack_IsAnHourLongerThanItReads()
    {
        // 2026-11-01: 02:00 becomes 01:00. Six hours on the wall, seven in real time - and the
        // worker really is at work for both passes of the repeated hour.
        var window = Window(new DateOnly(2026, 11, 1), 0, 6);

        Assert.Equal(TimeSpan.FromHours(7), window.Duration);
    }

    [Fact]
    public void EdgesInsideTheRepeatedHour_TakeTheFirstOccurrenceToOpenAndTheLastToClose()
    {
        // 01:30 happens twice on this date. The asymmetry is the reason the port resolves a window
        // rather than a point: a single "wall clock to instant" method has to pick one occurrence,
        // and either choice is wrong at one of the two edges.
        var window = _resolver.ToInstantWindow(
            NewYork, new DateOnly(2026, 11, 1), new TimeOnly(1, 30), new TimeOnly(1, 45));

        Assert.NotNull(window);

        // Opens on the first pass (05:30Z, still EDT), closes on the second (06:45Z, EST) - so the
        // window is an hour and a quarter, not fifteen minutes. Picking one occurrence for both
        // edges would have deleted an hour of a real working day.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), window.Value.StartsAt.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 45, 0, TimeSpan.Zero), window.Value.EndsAt.ToUniversalTime());
    }

    [Fact]
    public void ToLocalDate_IsTheBusinessDay_NotTheUtcDay()
    {
        // 01:00Z on 11 July is 21:00 the previous evening in New York. A horizon counted from the
        // UTC date would start a day late for every calendar west of Greenwich, for part of every
        // day - which is why the materialiser asks this and never DateOnly.FromDateTime(now).
        var instant = new DateTimeOffset(2026, 7, 11, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 7, 10), _resolver.ToLocalDate(NewYork, instant));
    }

    /// <summary>`25-145`: <c>ToLocal</c> re-expresses the same instant with the destination zone's own
    /// offset - New York in January is standard time (-05:00), so 12:00Z reads 07:00 local, and the
    /// two <see cref="DateTimeOffset"/> values still compare equal as instants even though their own
    /// <c>Offset</c>/<c>Hour</c> differ.</summary>
    [Fact]
    public void ToLocal_ReExpressesTheSameInstantWithTheZonesOwnOffset()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var local = _resolver.ToLocal(NewYork, instant);

        Assert.Equal(instant, local);
        Assert.Equal(TimeSpan.FromHours(-5), local.Offset);
        Assert.Equal(7, local.Hour);
    }

    /// <summary>
    /// `25-145`'s own explicit concern, checked live rather than assumed: `25-26` already found a
    /// deployed base image shipping no tzdata at all, so a booking calendar's own configured zone
    /// resolving successfully on a developer's machine proves nothing about a container image. Every
    /// one of the eleven canonical Russian federal zones this item's own <c>ModuleStepFactory</c>
    /// renders an abbreviation for (`25-16`'s own outcome; <c>ago-console</c>'s <c>calendarFormat.tsx</c>
    /// names the identical eleven) must resolve through the exact same <see cref="SystemWallClockResolver"/>
    /// this suite already exercises against <c>America/New_York</c> above - the same code path
    /// <c>ReplyToModuleTaskHandler</c> calls in production, not a coincidentally-working substitute.
    /// </summary>
    [Theory]
    [InlineData("Europe/Kaliningrad")]
    [InlineData("Europe/Moscow")]
    [InlineData("Europe/Samara")]
    [InlineData("Asia/Yekaterinburg")]
    [InlineData("Asia/Omsk")]
    [InlineData("Asia/Krasnoyarsk")]
    [InlineData("Asia/Irkutsk")]
    [InlineData("Asia/Yakutsk")]
    [InlineData("Asia/Vladivostok")]
    [InlineData("Asia/Magadan")]
    [InlineData("Asia/Kamchatka")]
    public void EveryCanonicalRussianFederalZone_ResolvesLiveOnThisHost(string ianaId)
    {
        var zone = new CalendarTimeZone(ianaId);
        var instant = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var local = _resolver.ToLocal(zone, instant);

        // No DST since 2014 (`24-17`): the offset is fixed, so any real value at all - not a specific
        // one - is what proves FindSystemTimeZoneById resolved rather than silently falling through
        // to a caught exception this test would otherwise never see (ToLocal has no fallback branch;
        // an unresolvable zone throws UnknownCalendarTimeZoneException instead of reaching this line).
        Assert.NotEqual(TimeSpan.Zero, local.Offset);
        Assert.Equal(instant, local);
    }

    [Fact]
    public void AZoneTheHostCannotResolve_IsAnInfrastructureFaultWithItsOwnName()
    {
        // Well-formed (CalendarTimeZone accepted it) but not in any tz database. The caller must be
        // able to tell "this deployment is missing tzdata" from "a user typed nonsense", and
        // TimeZoneNotFoundException reaching a handler would also be an Infrastructure type leaking
        // through a port.
        var zone = new CalendarTimeZone("Mars/Olympus_Mons");

        var failure = Assert.Throws<UnknownCalendarTimeZoneException>(
            () => _resolver.ToInstantWindow(zone, new DateOnly(2026, 5, 4), new TimeOnly(9, 0), new TimeOnly(18, 0)));

        Assert.Equal(zone, failure.Zone);
    }

    [Fact]
    public void AWindowThatClosesBeforeItOpens_IsRejectedOutright()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _resolver.ToInstantWindow(NewYork, new DateOnly(2026, 5, 4), new TimeOnly(18, 0), new TimeOnly(9, 0)));
    }

    private TimeSlot Window(DateOnly date, int opensAtHour, int closesAtHour) =>
        _resolver.ToInstantWindow(NewYork, date, new TimeOnly(opensAtHour, 0), new TimeOnly(closesAtHour, 0))
            ?? throw new InvalidOperationException("Expected a resolvable window.");
}
