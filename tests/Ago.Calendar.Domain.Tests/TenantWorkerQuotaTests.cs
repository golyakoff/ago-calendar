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

    /// <summary>`26-317`: the computed floor every tenant gets with no grant at all - see
    /// <see cref="Tenant.EffectiveWorkerQuota"/>'s own remarks for why this is computed rather than
    /// backfilled. A raw grant at or above <see cref="Tenant.DefaultWorkerQuota"/> passes through
    /// unchanged; below it, the floor wins.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void EffectiveWorkerQuota_IsTheGreaterOfTheRawGrantAndTheDefaultFloor(int rawGrant, int expected)
    {
        var tenant = CalendarFixtures.Tenant();
        tenant.GrantWorkerQuota(rawGrant);

        Assert.Equal(expected, tenant.EffectiveWorkerQuota);

        // The floor never rewrites what was actually granted - the raw column stays the audit trail.
        Assert.Equal(rawGrant, tenant.WorkerQuota);
    }
}
