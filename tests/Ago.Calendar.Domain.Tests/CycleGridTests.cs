namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-14`'s pure day-selection rule, tested with no database and no clock in sight - exactly the
/// point of putting it in Domain. The two named cases from the item's own Done-when: "2 через 2" and
/// "сутки через трое".
/// </summary>
public sealed class CycleGridTests
{
    // A Monday, chosen so "the anchor day itself" and "a resting day" both land on named weekdays a
    // reader can check without counting.
    private static readonly DateOnly Monday = new(2026, 3, 2);

    [Fact]
    public void TwoOnTwoOff_AnchoredOnMonday_MarksMondayAndTuesdayWorking()
    {
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday));
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(1)));
    }

    [Fact]
    public void TwoOnTwoOff_AnchoredOnMonday_MarksWednesdayAndThursdayResting()
    {
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(2)));
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(3)));
    }

    [Fact]
    public void TwoOnTwoOff_RepeatsEveryFourDays()
    {
        // Day 4 is a fresh cycle's own anchor-equivalent day - working again, not a continuation of
        // the rest block.
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(4)));
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(5)));
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(6)));
    }

    [Fact]
    public void OneOnThreeOff_ProducesExactlyOneWorkingDayInEveryFour()
    {
        // "Сутки через трое" - the author's own second named case.
        var workingDays = 0;
        for (var offset = 0; offset < 40; offset++)
        {
            if (CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday.AddDays(offset)))
            {
                workingDays++;
            }
        }

        Assert.Equal(10, workingDays);
    }

    [Fact]
    public void OneOnThreeOff_MarksExactlyTheAnchorDayOfEveryFourDayBlock()
    {
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday));
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday.AddDays(1)));
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday.AddDays(2)));
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday.AddDays(3)));
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 3, day: Monday.AddDays(4)));
    }

    [Fact]
    public void ADayBeforeTheAnchor_IsStillResolvedCorrectly()
    {
        // The proper-modulo case: an offset of -1 must not throw and must not be treated as "working"
        // by accident of C#'s truncating %. Monday - 1 = Sunday, which is a resting day one before a
        // fresh 2/2 cycle would open on the following Monday.
        Assert.False(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(-1)));

        // Monday - 4 is a working day: exactly one full cycle before the anchor, so it aligns with
        // the anchor's own position.
        Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: 2, day: Monday.AddDays(-4)));
    }

    [Fact]
    public void ZeroRestDays_MeansWorkingEveryDay()
    {
        for (var offset = 0; offset < 10; offset++)
        {
            Assert.True(CycleGrid.IsWorkingDay(Monday, workingDays: 1, restDays: 0, day: Monday.AddDays(offset)));
        }
    }

    [Fact]
    public void ZeroWorkingDays_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CycleGrid.IsWorkingDay(Monday, workingDays: 0, restDays: 2, day: Monday));
    }

    [Fact]
    public void NegativeRestDays_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CycleGrid.IsWorkingDay(Monday, workingDays: 2, restDays: -1, day: Monday));
    }
}
