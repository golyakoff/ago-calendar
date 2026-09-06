namespace Ago.Calendar.Domain.Tests;

/// <summary>`22-07`'s own stated rule, proven directly against the pure function rather than only
/// through a real-Postgres round trip - see `Ago.Calendar.Integration.Tests` for the version of this
/// same proof that goes through <c>IWorkerQuotaGrantStore</c> and a real transaction.</summary>
public class WorkerQuotaPolicyTests
{
    private static readonly DateTimeOffset Now = CalendarFixtures.Now;

    [Fact]
    public void SelectWorkersToDeactivate_WhenActiveCountFitsTheQuota_SelectsNobody()
    {
        var tenant = CalendarFixtures.Tenant();
        var workers = new[] { CalendarFixtures.Worker(tenant), CalendarFixtures.Worker(tenant) };

        var selected = WorkerQuotaPolicy.SelectWorkersToDeactivate(workers, quota: 2);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectWorkersToDeactivate_WhenQuotaIsLowered_DeactivatesTheMostRecentlyCreatedFirst()
    {
        var tenant = CalendarFixtures.Tenant();
        var oldest = Worker.Create(new WorkerId(Guid.CreateVersion7(Now)), tenant.Id, "One", "A", null, Now);
        var middle = Worker.Create(new WorkerId(Guid.CreateVersion7(Now.AddSeconds(1))), tenant.Id, "Two", "B", null, Now.AddSeconds(1));
        var newest = Worker.Create(new WorkerId(Guid.CreateVersion7(Now.AddSeconds(2))), tenant.Id, "Three", "C", null, Now.AddSeconds(2));

        // Deliberately not created in chronological order, to prove the policy sorts rather than
        // trusting whatever order it was handed - the exact "picking by row order" mistake this
        // item's own backlog item refused to allow.
        var selected = WorkerQuotaPolicy.SelectWorkersToDeactivate([middle, newest, oldest], quota: 2);

        var deactivated = Assert.Single(selected);
        Assert.Same(newest, deactivated);
    }

    [Fact]
    public void SelectWorkersToDeactivate_LoweringByTwo_KeepsOnlyTheOldest()
    {
        var tenant = CalendarFixtures.Tenant();
        var oldest = Worker.Create(new WorkerId(Guid.CreateVersion7(Now)), tenant.Id, "One", "A", null, Now);
        var middle = Worker.Create(new WorkerId(Guid.CreateVersion7(Now.AddSeconds(1))), tenant.Id, "Two", "B", null, Now.AddSeconds(1));
        var newest = Worker.Create(new WorkerId(Guid.CreateVersion7(Now.AddSeconds(2))), tenant.Id, "Three", "C", null, Now.AddSeconds(2));

        var selected = WorkerQuotaPolicy.SelectWorkersToDeactivate([oldest, middle, newest], quota: 1);

        Assert.Equal(2, selected.Count);
        Assert.Contains(newest, selected);
        Assert.Contains(middle, selected);
        Assert.DoesNotContain(oldest, selected);
    }

    [Fact]
    public void SelectWorkersToDeactivate_WhenQuotaIsZero_DeactivatesEveryone()
    {
        var tenant = CalendarFixtures.Tenant();
        var workers = new[] { CalendarFixtures.Worker(tenant), CalendarFixtures.Worker(tenant) };

        var selected = WorkerQuotaPolicy.SelectWorkersToDeactivate(workers, quota: 0);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectWorkersToDeactivate_TiedCreatedAt_BreaksOnIdDescending()
    {
        var tenant = CalendarFixtures.Tenant();
        var lowerId = Worker.Create(new WorkerId(new Guid("00000000-0000-0000-0000-000000000001")), tenant.Id, "One", "A", null, Now);
        var higherId = Worker.Create(new WorkerId(new Guid("00000000-0000-0000-0000-000000000002")), tenant.Id, "Two", "B", null, Now);

        var selected = WorkerQuotaPolicy.SelectWorkersToDeactivate([lowerId, higherId], quota: 1);

        var deactivated = Assert.Single(selected);
        Assert.Same(higherId, deactivated);
    }

    [Fact]
    public void SelectWorkersToDeactivate_RejectsANegativeQuota()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerQuotaPolicy.SelectWorkersToDeactivate([], quota: -1));
    }
}
