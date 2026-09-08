namespace Ago.Calendar.Domain.Tests;

/// <summary>`23-60`/`adr/0147`: the two invariants a merge leans on -
/// <see cref="Customer.AbsorbHistoryFrom"/> combines history without ever overwriting a fact the
/// survivor already had, and <see cref="Customer.MarkMergedInto"/> can only ever fire once per row,
/// which is this aggregate's own statement of "a merge is irreversible" (`adr/0147`'s asymmetry
/// argument, and this item's own answer to the undo question it left open).</summary>
public class CustomerMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AbsorbHistoryFrom_AddsTheNoShowCounts_RatherThanReplacing()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        survivor.RecordNoShow(Now);
        absorbed.RecordNoShow(Now);
        absorbed.RecordNoShow(Now);

        survivor.AbsorbHistoryFrom(absorbed, Now);

        Assert.Equal(3, survivor.NoShowCount);
    }

    [Fact]
    public void AbsorbHistoryFrom_WhenSurvivorHasNoPhoneVerification_TakesTheAbsorbedRowsOwn()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        absorbed.RecordVerifiedPhone(Now);

        survivor.AbsorbHistoryFrom(absorbed, Now);

        Assert.Equal(Now, survivor.PhoneVerifiedAt);
    }

    [Fact]
    public void AbsorbHistoryFrom_WhenSurvivorAlreadyHasPhoneVerification_NeverOverwritesIt()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        var earlier = Now.AddDays(-10);
        survivor.RecordVerifiedPhone(earlier);
        absorbed.RecordVerifiedPhone(Now);

        survivor.AbsorbHistoryFrom(absorbed, Now);

        Assert.Equal(earlier, survivor.PhoneVerifiedAt);
    }

    [Fact]
    public void AbsorbHistoryFrom_NeverTouchesDisplayNameOrNotes()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        absorbed.Describe("Anna the absorbed one", "a note nobody asked to carry over");

        survivor.AbsorbHistoryFrom(absorbed, Now);

        Assert.Null(survivor.DisplayName);
        Assert.Null(survivor.Notes);
    }

    [Fact]
    public void AbsorbHistoryFrom_MovesTheLastSeenWatermarkForward()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        var mergedAt = Now.AddDays(1);

        survivor.AbsorbHistoryFrom(absorbed, mergedAt);

        Assert.Equal(mergedAt, survivor.LastSeenAt);
    }

    [Fact]
    public void AbsorbHistoryFrom_GivenItself_Throws()
    {
        var tenant = CalendarFixtures.Tenant();
        var customer = CalendarFixtures.Customer(tenant);

        Assert.Throws<InvalidOperationException>(() => customer.AbsorbHistoryFrom(customer, Now));
    }

    [Fact]
    public void MarkMergedInto_SetsBothFields()
    {
        var tenant = CalendarFixtures.Tenant();
        var survivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);

        absorbed.MarkMergedInto(survivor.Id, Now);

        Assert.Equal(survivor.Id, absorbed.MergedIntoCustomerId);
        Assert.Equal(Now, absorbed.MergedAt);
    }

    [Fact]
    public void MarkMergedInto_CalledTwice_Throws_BecauseAMergeIsIrreversible()
    {
        var tenant = CalendarFixtures.Tenant();
        var firstSurvivor = CalendarFixtures.Customer(tenant);
        var secondSurvivor = CalendarFixtures.Customer(tenant);
        var absorbed = CalendarFixtures.Customer(tenant);
        absorbed.MarkMergedInto(firstSurvivor.Id, Now);

        Assert.Throws<InvalidOperationException>(() => absorbed.MarkMergedInto(secondSurvivor.Id, Now.AddMinutes(1)));
    }

    [Fact]
    public void MarkMergedInto_GivenItsOwnId_Throws()
    {
        var tenant = CalendarFixtures.Tenant();
        var customer = CalendarFixtures.Customer(tenant);

        Assert.Throws<InvalidOperationException>(() => customer.MarkMergedInto(customer.Id, Now));
    }
}
