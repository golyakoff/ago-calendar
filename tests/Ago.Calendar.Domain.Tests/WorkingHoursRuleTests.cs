namespace Ago.Calendar.Domain.Tests;

public class WorkingHoursRuleTests
{
    [Fact]
    public void For_WhenWorkerDoesNotParticipateInThatCalendar_Throws()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = CalendarFixtures.Worker(tenant);
        var joined = CalendarFixtures.Calendar(tenant);
        var other = CalendarFixtures.Calendar(tenant);
        worker.JoinCalendar(joined);

        // The v1 ceiling seen from the other side: a worker cannot be given hours in a second
        // calendar, because they cannot be in one.
        Assert.Throws<WorkerCalendarLimitException>(() => WorkingHoursRule.For(
            NewRuleId(), worker, other, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
    }

    [Fact]
    public void For_WhenWorkerAndCalendarBelongToDifferentTenants_Throws()
    {
        var mine = CalendarFixtures.Tenant("Mine");
        var theirs = CalendarFixtures.Tenant("Theirs");
        var worker = CalendarFixtures.Worker(mine);

        Assert.Throws<TenantMismatchException>(() => WorkingHoursRule.For(
            NewRuleId(), worker, CalendarFixtures.Calendar(theirs),
            DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
    }

    [Fact]
    public void For_WhenTheDayEndsBeforeItStarts_Throws()
    {
        var (worker, calendar) = JoinedWorker();

        // A shift crossing midnight is two rules on two days - see the factory's own remarks.
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkingHoursRule.For(
            NewRuleId(), worker, calendar, DayOfWeek.Friday, new TimeOnly(22, 0), new TimeOnly(2, 0)));
    }

    [Fact]
    public void For_StoresWallClock_NotAnInstant()
    {
        var (worker, calendar) = JoinedWorker();

        var rule = WorkingHoursRule.For(
            NewRuleId(), worker, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0));

        // The assertion that matters is the *type*: a rule that carried a DateTimeOffset would have
        // had to pick an offset here, and any offset it picked would be wrong for half the year in a
        // DST zone. See DaylightSavingTimeTests for what that would actually cost.
        Assert.Equal(new TimeOnly(9, 0), rule.StartsAt);
        Assert.Equal(new TimeOnly(18, 0), rule.EndsAt);
        Assert.Equal(DayOfWeek.Monday, rule.DayOfWeek);
    }

    private static (Worker Worker, BookingCalendar Calendar) JoinedWorker()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = CalendarFixtures.Worker(tenant);
        var calendar = CalendarFixtures.Calendar(tenant);
        worker.JoinCalendar(calendar);
        return (worker, calendar);
    }

    private static WorkingHoursRuleId NewRuleId() =>
        new(Guid.CreateVersion7(CalendarFixtures.Now));
}
