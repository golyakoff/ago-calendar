namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-14`'s aggregate: the two template kinds, switching between them, the numeric bounds, and the
/// forward-only cursor - the riskiest guarantee this item adds, held here at the aggregate's own
/// level rather than only through an Application-layer test.
/// </summary>
public sealed class WorkerScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddDays(1);

    private static WorkerScheduleId NewScheduleId() => new(Guid.CreateVersion7(Now));

    private static WorkerId NewWorkerId() => new(Guid.CreateVersion7(Now));

    [Fact]
    public void CreateWeekly_SetsKindAndTheFourSharedNumbers_AndLeavesEveryCycleFieldNull()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 45, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        Assert.Equal(ScheduleKind.Weekly, schedule.Kind);
        Assert.Equal(45, schedule.SlotMinutes);
        Assert.Equal(10, schedule.BufferMinutes);
        Assert.Equal(30, schedule.HorizonDays);
        Assert.Equal(new DateOnly(2026, 3, 2), schedule.MaterializeFrom);
        Assert.Null(schedule.CycleAnchor);
        Assert.Null(schedule.CycleWorkingDays);
        Assert.Null(schedule.CycleRestDays);
        Assert.Null(schedule.CycleStartsAt);
        Assert.Null(schedule.CycleEndsAt);
        Assert.Equal(Now, schedule.CreatedAt);
        Assert.Equal(Now, schedule.UpdatedAt);
    }

    [Fact]
    public void CreateCycle_SetsTheCycleFields()
    {
        var anchor = new DateOnly(2026, 3, 2);
        var schedule = WorkerSchedule.CreateCycle(
            NewScheduleId(), NewWorkerId(), anchor, workingDays: 2, restDays: 2,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0),
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 60, materializeFrom: anchor, Now);

        Assert.Equal(ScheduleKind.Cycle, schedule.Kind);
        Assert.Equal(anchor, schedule.CycleAnchor);
        Assert.Equal(2, schedule.CycleWorkingDays);
        Assert.Equal(2, schedule.CycleRestDays);
        Assert.Equal(new TimeOnly(9, 0), schedule.CycleStartsAt);
        Assert.Equal(new TimeOnly(18, 0), schedule.CycleEndsAt);
    }

    [Fact]
    public void CreateCycle_RejectsEndsAtNotAfterStartsAt()
    {
        var anchor = new DateOnly(2026, 3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerSchedule.CreateCycle(
            NewScheduleId(), NewWorkerId(), anchor, workingDays: 1, restDays: 3,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(9, 0),
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 30, materializeFrom: anchor, Now));
    }

    [Fact]
    public void CreateCycle_RejectsZeroWorkingDays()
    {
        var anchor = new DateOnly(2026, 3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerSchedule.CreateCycle(
            NewScheduleId(), NewWorkerId(), anchor, workingDays: 0, restDays: 3,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0),
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 30, materializeFrom: anchor, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CreateWeekly_RejectsANonPositiveSlotLength(int slotMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes, bufferMinutes: 0, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now));
    }

    [Fact]
    public void CreateWeekly_RejectsABufferAboveEightHours()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 8 * 60 + 1, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now));
    }

    [Fact]
    public void CreateWeekly_RejectsAHorizonAboveOneEightyDays()
    {
        // The item's own Done-when: "A horizon above 180 is refused" - held here at the aggregate
        // itself so a direct API call cannot bypass it, not only in a console-side check.
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 181,
            materializeFrom: new DateOnly(2026, 3, 2), Now));
    }

    [Fact]
    public void CreateWeekly_AcceptsExactlyOneEightyDays()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 180,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        Assert.Equal(180, schedule.HorizonDays);
    }

    [Fact]
    public void ReconfigureCycle_SwitchesFromWeekly_AndSetsUpdatedAt()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 45, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        var anchor = new DateOnly(2026, 3, 2);
        schedule.ReconfigureCycle(
            anchor, workingDays: 1, restDays: 3, startsAt: new TimeOnly(8, 0), endsAt: new TimeOnly(20, 0),
            slotMinutes: 60, bufferMinutes: 15, horizonDays: 45, materializeFrom: anchor, Later);

        Assert.Equal(ScheduleKind.Cycle, schedule.Kind);
        Assert.Equal(anchor, schedule.CycleAnchor);
        Assert.Equal(1, schedule.CycleWorkingDays);
        Assert.Equal(3, schedule.CycleRestDays);
        Assert.Equal(60, schedule.SlotMinutes);
        Assert.Equal(Later, schedule.UpdatedAt);
        Assert.Equal(Now, schedule.CreatedAt);
    }

    [Fact]
    public void ReconfigureWeekly_SwitchingFromCycle_ClearsEveryCycleField()
    {
        var anchor = new DateOnly(2026, 3, 2);
        var schedule = WorkerSchedule.CreateCycle(
            NewScheduleId(), NewWorkerId(), anchor, workingDays: 2, restDays: 2,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0),
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 30, materializeFrom: anchor, Now);

        schedule.ReconfigureWeekly(
            slotMinutes: 45, bufferMinutes: 5, horizonDays: 20, materializeFrom: anchor, Later);

        Assert.Equal(ScheduleKind.Weekly, schedule.Kind);
        Assert.Null(schedule.CycleAnchor);
        Assert.Null(schedule.CycleWorkingDays);
        Assert.Null(schedule.CycleRestDays);
        Assert.Null(schedule.CycleStartsAt);
        Assert.Null(schedule.CycleEndsAt);
    }

    [Fact]
    public void AdvanceCursor_MovesMaterializeFromForward()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 10,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        schedule.AdvanceCursor(new DateOnly(2026, 3, 13), Later);

        Assert.Equal(new DateOnly(2026, 3, 13), schedule.MaterializeFrom);
        Assert.Equal(Later, schedule.UpdatedAt);
    }

    [Fact]
    public void AdvanceCursor_RefusesToMoveBackwards()
    {
        // THE guarantee this item names explicitly: moving the cursor backwards is `20-16`'s own
        // destructive re-cut, deliberately not reachable from here.
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 10,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.AdvanceCursor(new DateOnly(2026, 3, 2), Later));

        // And the refusal did not partially apply - UpdatedAt did not move either.
        Assert.Equal(new DateOnly(2026, 3, 13), schedule.MaterializeFrom);
        Assert.Equal(Now, schedule.UpdatedAt);
        Assert.Contains("backwards", exception.Message);
    }

    [Fact]
    public void ReconfigureWeekly_RefusesAMaterializeFromEarlierThanTheCurrentOne()
    {
        // The same guard, exercised through the console's own save path rather than the job's.
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 10,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.ReconfigureWeekly(
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 10, materializeFrom: new DateOnly(2026, 3, 1), Later));
    }

    [Fact]
    public void ReconfigureWeekly_AcceptsTheSameMaterializeFromUnchanged()
    {
        // Forward-only means "never earlier", not "strictly later" - re-saving the same cursor value
        // (every other field changing) must not be refused as a regression.
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 10,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        schedule.ReconfigureWeekly(
            slotMinutes: 60, bufferMinutes: 5, horizonDays: 20, materializeFrom: new DateOnly(2026, 3, 13), Later);

        Assert.Equal(new DateOnly(2026, 3, 13), schedule.MaterializeFrom);
        Assert.Equal(60, schedule.SlotMinutes);
    }

    /// <summary>`20-18`'s own default, restated as a test: a schedule created without naming the flag
    /// gets the author's own stated default - buffers count.</summary>
    [Fact]
    public void CreateWeekly_DefaultsBuffersCountTowardServiceDurationToTrue()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        Assert.True(schedule.BuffersCountTowardServiceDuration);
    }

    [Fact]
    public void CreateWeekly_CanSetBuffersCountTowardServiceDurationToFalse()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now, buffersCountTowardServiceDuration: false);

        Assert.False(schedule.BuffersCountTowardServiceDuration);
    }

    [Fact]
    public void ReconfigureWeekly_CanFlipBuffersCountTowardServiceDuration()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        schedule.ReconfigureWeekly(
            slotMinutes: 30, bufferMinutes: 10, horizonDays: 30, materializeFrom: new DateOnly(2026, 3, 2), Later,
            buffersCountTowardServiceDuration: false);

        Assert.False(schedule.BuffersCountTowardServiceDuration);
    }

    [Fact]
    public void RecutFrom_MovesMaterializeFromBackwards()
    {
        // `20-16`'s own entry point - the one, deliberate exception to the forward-only rule every
        // other method above enforces.
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        schedule.RecutFrom(new DateOnly(2026, 3, 2), Later);

        Assert.Equal(new DateOnly(2026, 3, 2), schedule.MaterializeFrom);
        Assert.Equal(Later, schedule.UpdatedAt);
    }

    [Fact]
    public void RecutFrom_RefusesACursorThatIsNotEarlierThanTheCurrentOne()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.RecutFrom(new DateOnly(2026, 3, 13), Later));

        // No partial application - a refused RecutFrom moves nothing, exactly like a refused
        // AdvanceCursor.
        Assert.Equal(new DateOnly(2026, 3, 13), schedule.MaterializeFrom);
        Assert.Equal(Now, schedule.UpdatedAt);
        Assert.Contains("backwards only", exception.Message);
    }

    [Fact]
    public void RecutFrom_RefusesACursorLaterThanTheCurrentOne()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.RecutFrom(new DateOnly(2026, 3, 20), Later));
    }

    [Fact]
    public void RecutFrom_TouchesOnlyTheCursorAndUpdatedAt_NeverTheTemplate()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 45, bufferMinutes: 10, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 13), Now);

        schedule.RecutFrom(new DateOnly(2026, 3, 2), Later);

        // A re-cut regenerates what is already there from the template that is already active - it
        // is not a second way to change the template itself.
        Assert.Equal(45, schedule.SlotMinutes);
        Assert.Equal(10, schedule.BufferMinutes);
        Assert.Equal(30, schedule.HorizonDays);
        Assert.Equal(ScheduleKind.Weekly, schedule.Kind);
    }

    [Fact]
    public void IsCycleWorkingDay_DelegatesToCycleGrid()
    {
        var anchor = new DateOnly(2026, 3, 2);
        var schedule = WorkerSchedule.CreateCycle(
            NewScheduleId(), NewWorkerId(), anchor, workingDays: 2, restDays: 2,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0),
            slotMinutes: 30, bufferMinutes: 0, horizonDays: 30, materializeFrom: anchor, Now);

        Assert.True(schedule.IsCycleWorkingDay(anchor));
        Assert.False(schedule.IsCycleWorkingDay(anchor.AddDays(2)));
    }

    [Fact]
    public void IsCycleWorkingDay_ThrowsOnAWeeklySchedule()
    {
        var schedule = WorkerSchedule.CreateWeekly(
            NewScheduleId(), NewWorkerId(), slotMinutes: 30, bufferMinutes: 0, horizonDays: 30,
            materializeFrom: new DateOnly(2026, 3, 2), Now);

        Assert.Throws<InvalidOperationException>(() => schedule.IsCycleWorkingDay(new DateOnly(2026, 3, 2)));
    }
}
