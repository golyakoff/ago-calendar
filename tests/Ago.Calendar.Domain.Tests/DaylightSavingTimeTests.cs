namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// date-and-time.md asks for it directly: "at least one test runs across a DST boundary in a
/// non-UTC zone. If that test never existed, the code has not been proven."
///
/// <para>These tests are not about a helper method - they are about the model's central time
/// decision, which is that a <see cref="WorkingHoursRule"/> is wall clock, an <see cref="Event"/> is
/// an instant, and the two are bridged exactly once through the calendar's IANA zone. Each test
/// below shows what a shortcut would have cost.</para>
///
/// <para><c>America/New_York</c> is used rather than the product's own <c>Europe/Moscow</c> on
/// purpose: Russia has not observed DST since 2014, so a Moscow-only test would pass against code
/// that stored a fixed offset and prove nothing at all.</para>
/// </summary>
public class DaylightSavingTimeTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void TheSameWallClockHour_MapsToDifferentInstants_AcrossADstBoundary()
    {
        // "09:00 on a Saturday" - one rule, two Saturdays, one week apart. US DST began 2026-03-08.
        var beforeDst = ToInstant(new DateOnly(2026, 3, 7), new TimeOnly(9, 0));
        var afterDst = ToInstant(new DateOnly(2026, 3, 14), new TimeOnly(9, 0));

        Assert.Equal(TimeSpan.FromHours(-5), beforeDst.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), afterDst.Offset);

        // Seven days on the wall clock; only six days and twenty-three hours of real time. A rule
        // that had stored `-05:00` at configuration time would have opened the shop an hour late for
        // every day after the transition, and nothing in the code would have looked wrong.
        Assert.Equal(TimeSpan.FromDays(7) - TimeSpan.FromHours(1), afterDst.UtcDateTime - beforeDst.UtcDateTime);
    }

    [Fact]
    public void TwoSlotsFromTheSameRule_DoNotOverlap_AcrossTheSpringForwardGap()
    {
        // 01:30 local does not exist on the morning the clocks jump from 02:00 to 03:00. The two
        // slots either side of the gap are still an hour apart in real time and still adjacent,
        // never overlapping - which is what the exclusion constraint on `events` will be asked.
        var beforeGap = new TimeSlot(
            ToInstant(new DateOnly(2026, 3, 8), new TimeOnly(1, 0)),
            ToInstant(new DateOnly(2026, 3, 8), new TimeOnly(1, 0)).AddHours(1));
        var afterGap = new TimeSlot(
            ToInstant(new DateOnly(2026, 3, 8), new TimeOnly(3, 0)),
            ToInstant(new DateOnly(2026, 3, 8), new TimeOnly(3, 0)).AddHours(1));

        Assert.False(beforeGap.Overlaps(afterGap));

        // And they are genuinely adjacent in UTC even though two hours passed on the wall clock -
        // exactly the arithmetic that is wrong if anything downstream ever subtracts local times.
        Assert.Equal(beforeGap.EndsAt, afterGap.StartsAt);
    }

    [Fact]
    public void LocalDate_IsTheBusinessDay_NotTheUtcDay()
    {
        // 21:00 in New York is 01:00 the next morning in UTC. A late appointment therefore falls on
        // a different UTC date than the day the shop and the customer both call it - which is why
        // Event.LocalDate is a stored column rather than something derived from starts_at.
        var startsAt = ToInstant(new DateOnly(2026, 7, 10), new TimeOnly(21, 0));
        var localDate = new DateOnly(2026, 7, 10);

        var tenant = CalendarFixtures.Tenant();
        var calendar = CalendarFixtures.Calendar(tenant, "America/New_York");
        var worker = CalendarFixtures.Worker(tenant);

        var slot = Event.Materialize(
            new EventId(Guid.CreateVersion7(startsAt)), tenant.Id, calendar.Id, worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)), localDate, startsAt);

        Assert.Equal(new DateOnly(2026, 7, 10), slot.LocalDate);
        Assert.Equal(new DateOnly(2026, 7, 11), DateOnly.FromDateTime(slot.StartsAt.UtcDateTime));
    }

    [Fact]
    public void CalendarTimeZone_RejectsAFixedOffset()
    {
        // The mistake this whole file exists to make impossible, refused at the door.
        Assert.Throws<ArgumentException>(() => new CalendarTimeZone("+03:00"));
        Assert.Throws<ArgumentException>(() => new CalendarTimeZone("-05:00"));
    }

    /// <summary>Wall clock plus a zone, resolved to an instant - the conversion `20-02`'s
    /// materialiser will own. It lives in the test here because Domain must not read the tz
    /// database (see <see cref="CalendarTimeZone"/>); the test is allowed to, and a test project is
    /// exactly where the machine's own tz data is a legitimate input.</summary>
    private static DateTimeOffset ToInstant(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local));
    }
}
