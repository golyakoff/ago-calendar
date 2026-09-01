namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-18`'s own decision, held at the aggregate-free level it can be: the arithmetic
/// (<see cref="ConsecutiveRunFinder.ComputeSlotsNeeded"/>) and the walk
/// (<see cref="ConsecutiveRunFinder.FindRun"/>), both pure and both provable with no database. The
/// item's own Done-when names two exact cases - the 70/30/10 example, both ways - and this file proves
/// both of them with the item's own numbers, not rounded-off stand-ins.
/// </summary>
public sealed class ConsecutiveRunFinderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly TenantId TenantId = new(Guid.CreateVersion7(Now));
    private static readonly CalendarId CalendarId = new(Guid.CreateVersion7(Now));
    private static readonly WorkerId WorkerId = new(Guid.CreateVersion7(Now));
    private static readonly DateOnly LocalDate = DateOnly.FromDateTime(Now.UtcDateTime);

    [Theory]
    // The item's own worked example, both ways.
    [InlineData(70, 30, 10, true, 2)]
    [InlineData(70, 30, 10, false, 3)]
    // The degenerate case: zero buffer is simply adjacent slots, and both readings agree.
    [InlineData(60, 30, 0, true, 2)]
    [InlineData(60, 30, 0, false, 2)]
    // A service that fits one slot needs exactly one, regardless of the setting.
    [InlineData(30, 30, 10, true, 1)]
    [InlineData(20, 30, 10, false, 1)]
    public void ComputeSlotsNeeded_MatchesTheWorkedArithmetic(
        int durationMinutes, int slotMinutes, int bufferMinutes, bool buffersCount, int expected)
    {
        var slots = ConsecutiveRunFinder.ComputeSlotsNeeded(durationMinutes, slotMinutes, bufferMinutes, buffersCount);

        Assert.Equal(expected, slots);
    }

    /// <summary>The item's own Done-when, stated exactly: "two slots ending 13:10 with the setting on,
    /// three slots ending 13:50 with it off" - a 70-minute service, 12:00 start, 30-minute slots,
    /// 10-minute buffer.</summary>
    [Theory]
    [InlineData(true, 2, 13, 10)]
    [InlineData(false, 3, 13, 50)]
    public void FindRun_The70_30_10Example_EndsExactlyWhereTheItemSaysItShould(
        bool buffersCount, int expectedSlotCount, int expectedEndHour, int expectedEndMinute)
    {
        var day = ARun(startHour: 12, count: 4, slotMinutes: 30, bufferMinutes: 10);

        var run = ConsecutiveRunFinder.FindRun(
            day, day[0].Id, serviceDurationMinutes: 70, slotMinutes: 30, bufferMinutes: 10,
            buffersCountTowardServiceDuration: buffersCount);

        Assert.NotNull(run);
        Assert.Equal(expectedSlotCount, run.Count);
        Assert.Equal(day.Take(expectedSlotCount).Select(e => e.Id), run);

        var lastSlot = day.Single(e => e.Id == run[^1]);
        Assert.Equal(new TimeOnly(expectedEndHour, expectedEndMinute), TimeOnly.FromDateTime(lastSlot.EndsAt.UtcDateTime));
    }

    [Fact]
    public void FindRun_WhenTheStartingSlotIsNotAvailable_ReturnsNull()
    {
        var day = ARun(startHour: 12, count: 3, slotMinutes: 30, bufferMinutes: 10);
        day[0].Claim(new CustomerId(Guid.CreateVersion7(Now)), new ServiceId(Guid.CreateVersion7(Now)), Now, Now.AddMinutes(15));

        var run = ConsecutiveRunFinder.FindRun(day, day[0].Id, 30, 30, 10, true);

        Assert.Null(run);
    }

    /// <summary>The item's own Done-when: "a run whose middle slot is already taken is neither offered
    /// nor claimable when requested directly by id."</summary>
    [Fact]
    public void FindRun_WhenTheMiddleSlotOfAnOtherwiseValidRunIsTaken_ReturnsNull()
    {
        var day = ARun(startHour: 12, count: 3, slotMinutes: 30, bufferMinutes: 10);
        day[1].Claim(new CustomerId(Guid.CreateVersion7(Now)), new ServiceId(Guid.CreateVersion7(Now)), Now, Now.AddMinutes(15));

        // 70 minutes with a 10-minute buffer needs two slots when counted - day[0] and day[1] - and
        // day[1] is gone.
        var run = ConsecutiveRunFinder.FindRun(day, day[0].Id, 70, 30, 10, buffersCountTowardServiceDuration: true);

        Assert.Null(run);
    }

    [Fact]
    public void FindRun_WhenTheDayEndsBeforeTheRunCompletes_ReturnsNull()
    {
        // Only two slots exist; a service needing three has nowhere left to go.
        var day = ARun(startHour: 12, count: 2, slotMinutes: 30, bufferMinutes: 10);

        var run = ConsecutiveRunFinder.FindRun(day, day[0].Id, 70, 30, 10, buffersCountTowardServiceDuration: false);

        Assert.Null(run);
    }

    [Fact]
    public void FindRun_WhenABufferGapDoesNotMatch_ReturnsNull()
    {
        // A hand-edited day: the second slot starts five minutes later than the grid's own buffer
        // would put it - a boundary edit or a block having reshaped the grid. The exact-equality rule
        // is what refuses to guess through it.
        var start = Now.AddHours(3);
        var first = Event.Materialize(
            new EventId(Guid.CreateVersion7(Now)), TenantId, CalendarId, WorkerId,
            new TimeSlot(start, start.AddMinutes(30)), LocalDate, Now);
        var misalignedStart = start.AddMinutes(30 + 10).AddMinutes(5);
        var second = Event.Materialize(
            new EventId(Guid.CreateVersion7(Now)), TenantId, CalendarId, WorkerId,
            new TimeSlot(misalignedStart, misalignedStart.AddMinutes(30)), LocalDate, Now);

        var run = ConsecutiveRunFinder.FindRun([first, second], first.Id, 60, 30, 10, buffersCountTowardServiceDuration: true);

        Assert.Null(run);
    }

    [Fact]
    public void FindRun_WhenTheStartingIdIsNotInTheDay_ReturnsNull()
    {
        var day = ARun(startHour: 12, count: 2, slotMinutes: 30, bufferMinutes: 10);
        var foreignId = new EventId(Guid.CreateVersion7(Now));

        var run = ConsecutiveRunFinder.FindRun(day, foreignId, 30, 30, 10, true);

        Assert.Null(run);
    }

    [Fact]
    public void FindRun_ASingleSlotService_NeedsNoSuccessorAtAll()
    {
        // The ordinary case, unaffected: a service that fits one slot claims exactly one, and this
        // handler never even looks past it.
        var day = ARun(startHour: 12, count: 1, slotMinutes: 30, bufferMinutes: 10);

        var run = ConsecutiveRunFinder.FindRun(day, day[0].Id, 30, 30, 10, true);

        Assert.Equal([day[0].Id], run);
    }

    private static List<Event> ARun(int startHour, int count, int slotMinutes, int bufferMinutes)
    {
        var slots = new List<Event>(count);
        var start = new DateTimeOffset(Now.Year, Now.Month, Now.Day, startHour, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            slots.Add(Event.Materialize(
                new EventId(Guid.CreateVersion7(Now.AddSeconds(i))), TenantId, CalendarId, WorkerId,
                new TimeSlot(start, start.AddMinutes(slotMinutes)), LocalDate, Now));
            start = start.AddMinutes(slotMinutes + bufferMinutes);
        }

        return slots;
    }
}
