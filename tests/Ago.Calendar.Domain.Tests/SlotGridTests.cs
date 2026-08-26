namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// <see cref="SlotGrid"/> is pure arithmetic on absolute time, so every one of these runs without a
/// clock, a zone or a database - which is the point of having pushed the zone conversion out of it.
/// The DST behaviour that falls out of that split is proven in
/// <see cref="DaylightSavingTimeTests"/>, where the window itself is resolved.
/// </summary>
public class SlotGridTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SlotsAreBackToBack_WhenTheBufferIsZero()
    {
        var slots = SlotGrid.Fill(Window(hours: 3), TimeSpan.FromMinutes(60), TimeSpan.Zero);

        Assert.Equal(3, slots.Count);
        Assert.Equal(NineAm, slots[0].StartsAt);

        // Each slot starts exactly where the previous ended. Half-open intervals, so this is
        // adjacency and not an overlap - the same comparison the exclusion constraint makes.
        Assert.Equal(slots[0].EndsAt, slots[1].StartsAt);
        Assert.False(slots[0].Overlaps(slots[1]));
    }

    [Fact]
    public void TheBufferSeparatesConsecutiveSlots_AndCostsCapacity()
    {
        var slots = SlotGrid.Fill(Window(hours: 3), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(15));

        // 45 + 15 = 60, so three fit in three hours and the third ends at 11:45 - the last fifteen
        // minutes are the buffer that would have preceded a fourth.
        Assert.Equal(3, slots.Count);
        Assert.Equal(NineAm.AddMinutes(60), slots[1].StartsAt);
        Assert.Equal(NineAm.AddMinutes(165), slots[2].EndsAt);
    }

    [Fact]
    public void APartialSlotAtTheEndIsNotProduced()
    {
        // Two and a half hours, fifty-minute slots, ten-minute buffers: two whole slots and fifty
        // minutes left, which is not another slot. Rounding up would publish a booking the worker
        // cannot honour, and rounding the *worker's* day up is not a decision availability
        // generation gets to make.
        var slots = SlotGrid.Fill(Window(TimeSpan.FromMinutes(150)), TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(10));

        Assert.Equal(2, slots.Count);
        Assert.True(slots[^1].EndsAt <= NineAm.AddMinutes(150));
    }

    [Fact]
    public void AWindowShorterThanOneSlot_ProducesNothing()
    {
        var slots = SlotGrid.Fill(Window(TimeSpan.FromMinutes(30)), TimeSpan.FromMinutes(45), TimeSpan.Zero);

        Assert.Empty(slots);
    }

    [Fact]
    public void AWindowExactlyOneSlotLong_ProducesExactlyOne()
    {
        // The boundary case a `<` instead of a `<=` in the loop condition would silently lose - a
        // worker with a single-appointment day would get no slots at all.
        var slots = SlotGrid.Fill(Window(TimeSpan.FromMinutes(45)), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(10));

        Assert.Single(slots);
    }

    [Fact]
    public void ABufferLongerThanTheRemainingDay_StillTerminates()
    {
        var slots = SlotGrid.Fill(Window(hours: 8), TimeSpan.FromMinutes(30), TimeSpan.FromHours(5));

        // Two slots, five hours apart, and no third because the stride overshoots the window. The
        // loop's stride is slotLength + buffer and slotLength is positive, which is what guarantees
        // termination for every input rather than for the ones anybody happened to try.
        Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void AZeroOrNegativeSlotLengthIsRejected()
    {
        // Not defensiveness: a zero-length slot would make the loop's stride zero and hang the
        // materialisation job, and a negative one would produce inverted intervals TimeSlot itself
        // would then reject one at a time, halfway through a day.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SlotGrid.Fill(Window(hours: 3), TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SlotGrid.Fill(Window(hours: 3), TimeSpan.FromMinutes(-30), TimeSpan.Zero));
    }

    [Fact]
    public void ANegativeBufferIsRejected()
    {
        // A negative buffer is slots that overlap each other, which the storage-level constraint
        // would reject anyway - but as a failed batch halfway through a materialisation run rather
        // than as an argument error at the call site.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SlotGrid.Fill(Window(hours: 3), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(-5)));
    }

    private static TimeSlot Window(int hours) => Window(TimeSpan.FromHours(hours));

    private static TimeSlot Window(TimeSpan length) => new(NineAm, NineAm + length);
}
