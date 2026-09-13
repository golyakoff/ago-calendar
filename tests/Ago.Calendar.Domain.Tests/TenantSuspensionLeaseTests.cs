namespace Ago.Calendar.Domain.Tests;

/// <summary>`22-08`/`adr/0149` rule 1: the account-wide suspension's own lease projection - see
/// <see cref="Tenant.SuspensionValidUntil"/>'s own remarks for why it lives here rather than behind a
/// cache, and for why it is compared live inside <c>BookingStore.TryBookAsync</c>'s own claim
/// transaction rather than read beforehand.</summary>
public class TenantSuspensionLeaseTests
{
    [Fact]
    public void Register_StartsWithNoLeaseAtAll()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Null(tenant.SuspensionValidUntil);
    }

    [Fact]
    public void ApplySuspensionLease_SetsTheValue()
    {
        var tenant = CalendarFixtures.Tenant();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        tenant.ApplySuspensionLease(until);

        Assert.Equal(until, tenant.SuspensionValidUntil);
    }

    /// <summary>A snapshot, not a delta - the identical shape `GrantWorkerQuota`'s own tests already
    /// prove for the quota: reapplying the identical instant is a no-op, which is what makes an
    /// at-least-once redelivery (or `ago-chat`'s own periodic renewal, republishing on a schedule by
    /// design) safe to repeat.</summary>
    [Fact]
    public void ApplySuspensionLease_IsASnapshotNotADelta_ReapplyingTheSameValueIsANoOp()
    {
        var tenant = CalendarFixtures.Tenant();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        tenant.ApplySuspensionLease(until);
        tenant.ApplySuspensionLease(until);

        Assert.Equal(until, tenant.SuspensionValidUntil);
    }

    /// <summary>The lift path - `null` clears the lease immediately, the identical "the module is told
    /// the current fact, not a delta" reasoning applied to the one value that means "no active
    /// lease".</summary>
    [Fact]
    public void ApplySuspensionLease_WithNull_ClearsTheLease()
    {
        var tenant = CalendarFixtures.Tenant();
        tenant.ApplySuspensionLease(DateTimeOffset.UtcNow.AddMinutes(5));

        tenant.ApplySuspensionLease(null);

        Assert.Null(tenant.SuspensionValidUntil);
    }
}
