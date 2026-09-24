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

    [Fact]
    public void ChangeTo_CorrectsTheThreeFieldsAHumanTypes()
    {
        // `26-97`'s whole promise at the aggregate: the 09:00 that should have been 19:00.
        var (worker, calendar) = JoinedWorker();
        var rule = WorkingHoursRule.For(
            NewRuleId(), worker, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(9, 30));

        rule.ChangeTo(worker, calendar, DayOfWeek.Tuesday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.Equal(DayOfWeek.Tuesday, rule.DayOfWeek);
        Assert.Equal(new TimeOnly(10, 0), rule.StartsAt);
        Assert.Equal(new TimeOnly(19, 0), rule.EndsAt);

        // Identity does not move - the rule is corrected, never reassigned.
        Assert.Equal(worker.Id, rule.WorkerId);
        Assert.Equal(calendar.Id, rule.CalendarId);
    }

    [Fact]
    public void ChangeTo_RefusesEverythingForRefuses()
    {
        // The point of the test is not the three throws; it is that they are the *same* three, from
        // the same Validate call. An edit path that accepted what creation rejects is how a rule that
        // ends before it starts gets into a database that has never accepted one.
        var tenant = CalendarFixtures.Tenant();
        var calendar = CalendarFixtures.Calendar(tenant);
        var worker = CalendarFixtures.Worker(tenant);
        worker.JoinCalendar(calendar);
        var rule = WorkingHoursRule.For(
            NewRuleId(), worker, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rule.ChangeTo(worker, calendar, DayOfWeek.Friday, new TimeOnly(22, 0), new TimeOnly(2, 0)));

        Assert.Throws<TenantMismatchException>(() => rule.ChangeTo(
            CalendarFixtures.Worker(CalendarFixtures.Tenant("Mine")),
            CalendarFixtures.Calendar(CalendarFixtures.Tenant("Theirs")),
            DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        // Same tenant, so the tenant check passes - and the membership check is the one that fires.
        var unjoined = CalendarFixtures.Worker(tenant);
        Assert.Throws<WorkerCalendarLimitException>(() => rule.ChangeTo(
            unjoined, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        // And nothing moved through any of them.
        Assert.Equal(DayOfWeek.Monday, rule.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), rule.StartsAt);
        Assert.Equal(new TimeOnly(18, 0), rule.EndsAt);
    }

    [Fact]
    public void ChangeTo_RefusesToMoveARuleToAnotherWorker()
    {
        // A correction is in place. Moving a rule to a second worker is indistinguishable from
        // deleting it and adding one, and pretending otherwise would give the aggregate a second
        // identity-changing door that WorkingHoursRule.For does not have.
        var tenant = CalendarFixtures.Tenant();
        var calendar = CalendarFixtures.Calendar(tenant);
        var worker = CalendarFixtures.Worker(tenant);
        var colleague = CalendarFixtures.Worker(tenant);
        worker.JoinCalendar(calendar);
        colleague.JoinCalendar(calendar);

        var rule = WorkingHoursRule.For(
            NewRuleId(), worker, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.Throws<WorkerCalendarLimitException>(() =>
            rule.ChangeTo(colleague, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
        Assert.Equal(worker.Id, rule.WorkerId);
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
