namespace Ago.Calendar.Domain.Tests;

/// <summary>`22-07`: the calendar add-on's own granted number, on the tenancy row - see
/// <see cref="Tenant.WorkerQuota"/>'s own remarks for why it lives here rather than behind a
/// cache.</summary>
public class TenantWorkerQuotaTests
{
    [Fact]
    public void Register_StartsWithNoGrantAtAll()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Equal(0, tenant.WorkerQuota);
    }

    [Fact]
    public void GrantWorkerQuota_SetsTheGrantedNumber()
    {
        var tenant = CalendarFixtures.Tenant();

        tenant.GrantWorkerQuota(5);

        Assert.Equal(5, tenant.WorkerQuota);
    }

    [Fact]
    public void GrantWorkerQuota_IsASnapshotNotADelta_ReapplyingTheSameValueIsANoOp()
    {
        var tenant = CalendarFixtures.Tenant();

        tenant.GrantWorkerQuota(5);
        tenant.GrantWorkerQuota(5);

        Assert.Equal(5, tenant.WorkerQuota);
    }

    [Fact]
    public void GrantWorkerQuota_CanLowerTheNumber()
    {
        var tenant = CalendarFixtures.Tenant();
        tenant.GrantWorkerQuota(5);

        tenant.GrantWorkerQuota(2);

        Assert.Equal(2, tenant.WorkerQuota);
    }

    [Fact]
    public void GrantWorkerQuota_RejectsANegativeNumber()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.GrantWorkerQuota(-1));
    }
}
