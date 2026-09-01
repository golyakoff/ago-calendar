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

    [Theory]
    [InlineData("   ", "Alex")]
    [InlineData("Doe", "   ")]
    public void Create_WithABlankRequiredNameField_Throws(string lastName, string firstName)
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Throws<ArgumentException>(() =>
            Worker.Create(
                new WorkerId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id, lastName, firstName, null,
                CalendarFixtures.Now));
    }

    [Fact]
    public void Create_DerivesTheDisplayNameAsFirstNameSpaceLastName_WithWhitespaceCollapsed()
    {
        // The stray spaces are the point of the test - a naive `$"{first} {last}"` concatenation
        // would leave them in.
        var tenant = CalendarFixtures.Tenant();

        var worker = Worker.Create(
            new WorkerId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id,
            "  Фамилия ", "  Имя  ", null, CalendarFixtures.Now);

        Assert.Equal("Имя Фамилия", worker.DisplayName);
        Assert.False(worker.DisplayNameIsCustom);
    }

    [Fact]
    public void Rename_BeforeAnyCustomDisplayNameIsSet_KeepsRecomputingIt()
    {
        var tenant = CalendarFixtures.Tenant();
        var worker = Worker.Create(
            new WorkerId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id,
            "Fox", "Robin", null, CalendarFixtures.Now);
        Assert.Equal("Robin Fox", worker.DisplayName);

        worker.Rename("Sparrow", "Robin", null, CalendarFixtures.Now.AddMinutes(1));

        Assert.Equal("Robin Sparrow", worker.DisplayName);
        Assert.False(worker.DisplayNameIsCustom);
    }

    [Fact]
    public void SetDisplayName_ThenRename_LeavesTheCustomDisplayNameUntouched()
    {
        // The one sequential test this item's own spec calls out by name: the same worker, three
        // calls in order, because two separate workers could each pass while the real "which happens
        // first" bug survives. Rename runs *after* SetDisplayName here on purpose - if the flag were
        // checked before it were raised, or raised before the first Rename's recomputation ran, this
        // would fail.
        var tenant = CalendarFixtures.Tenant();
        var worker = Worker.Create(
            new WorkerId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id,
            "Fox", "Robin", null, CalendarFixtures.Now);
        Assert.Equal("Robin Fox", worker.DisplayName);

        worker.SetDisplayName("Foxy", CalendarFixtures.Now.AddMinutes(1));
        Assert.Equal("Foxy", worker.DisplayName);
        Assert.True(worker.DisplayNameIsCustom);

        worker.Rename("Sparrow", "Robin", null, CalendarFixtures.Now.AddMinutes(2));

        // The name fields themselves still change - only DisplayName freezes.
        Assert.Equal("Sparrow", worker.LastName);
        Assert.Equal("Foxy", worker.DisplayName);
    }

    [Fact]
    public void UpdatedAt_MovesOnEveryMutation_AndCreatedAtNeverDoes()
    {
        var tenant = CalendarFixtures.Tenant();
        var created = CalendarFixtures.Now;
        var worker = Worker.Create(
            new WorkerId(Guid.CreateVersion7(created)), tenant.Id, "Fox", "Robin", null, created);
        Assert.Equal(created, worker.CreatedAt);
        Assert.Equal(created, worker.UpdatedAt);

        var renamedAt = created.AddDays(1);
        worker.Rename("Sparrow", "Robin", null, renamedAt);
        Assert.Equal(created, worker.CreatedAt);
        Assert.Equal(renamedAt, worker.UpdatedAt);

        var displayNamedAt = created.AddDays(2);
        worker.SetDisplayName("Sparrow the Barber", displayNamedAt);
        Assert.Equal(created, worker.CreatedAt);
        Assert.Equal(displayNamedAt, worker.UpdatedAt);

        var deactivatedAt = created.AddDays(3);
        worker.Deactivate(deactivatedAt);
        Assert.Equal(created, worker.CreatedAt);
        Assert.Equal(deactivatedAt, worker.UpdatedAt);

        var reactivatedAt = created.AddDays(4);
        worker.Reactivate(reactivatedAt);
        Assert.Equal(created, worker.CreatedAt);
        Assert.Equal(reactivatedAt, worker.UpdatedAt);
    }
}
