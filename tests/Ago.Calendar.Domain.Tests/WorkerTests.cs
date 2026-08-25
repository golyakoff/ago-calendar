namespace Ago.Calendar.Domain.Tests;

/// <summary>The tenancy and v1-ceiling invariants - the two rules that make a <see cref="Worker"/>
/// something other than a name in a table.</summary>
public class WorkerTests
{
    [Fact]
    public void JoinCalendar_WhenCalendarBelongsToAnotherTenant_Throws()
    {
        var mine = CalendarFixtures.Tenant("Mine");
        var theirs = CalendarFixtures.Tenant("Theirs");
        var worker = CalendarFixtures.Worker(mine);
        var foreignCalendar = CalendarFixtures.Calendar(theirs);

        Assert.Throws<TenantMismatchException>(() => worker.JoinCalendar(foreignCalendar));
    }

    [Fact]
    public void Offer_WhenServiceBelongsToAnotherTenant_Throws()
    {
        var mine = CalendarFixtures.Tenant("Mine");
        var theirs = CalendarFixtures.Tenant("Theirs");
        var worker = CalendarFixtures.Worker(mine);

        Assert.Throws<TenantMismatchException>(() => worker.Offer(CalendarFixtures.Service(theirs)));
    }

    [Fact]
    public void JoinCalendar_WhenAlreadyInAnotherCalendar_Throws_BecauseV1AllowsExactlyOne()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = CalendarFixtures.Worker(tenant);
        worker.JoinCalendar(CalendarFixtures.Calendar(tenant));

        Assert.Throws<WorkerCalendarLimitException>(() => worker.JoinCalendar(CalendarFixtures.Calendar(tenant)));
    }

    [Fact]
    public void JoinCalendar_WhenAlreadyInThatSameCalendar_IsANoOp()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = CalendarFixtures.Worker(tenant);
        var calendar = CalendarFixtures.Calendar(tenant);

        worker.JoinCalendar(calendar);
        worker.JoinCalendar(calendar);

        Assert.Single(worker.Calendars);
        Assert.True(worker.WorksIn(calendar.Id));
    }

    [Fact]
    public void Offer_Twice_IsANoOp()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = CalendarFixtures.Worker(tenant);
        var service = CalendarFixtures.Service(tenant);

        worker.Offer(service);
        worker.Offer(service);

        Assert.Single(worker.Services);
        Assert.True(worker.Offers(service.Id));
    }

    [Fact]
    public void Create_WithBlankDisplayName_Throws()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Throws<ArgumentException>(() =>
            Worker.Create(new WorkerId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id, "   "));
    }
}
