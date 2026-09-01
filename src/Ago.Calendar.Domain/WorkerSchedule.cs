namespace Ago.Calendar.Domain;

/// <summary>
/// A worker's own template for "when do they work, how long is a slot, and how far ahead is that
/// generated" - `20-14`. A separate aggregate from <see cref="Worker"/>, deliberately: this one is the
/// materialiser's input and it carries <see cref="MaterializeFrom"/>, a cursor the background job
/// writes on every run. Folding these fields into <see cref="Worker"/> would make `20-13`'s CRUD
/// screen save a column the job owns, and every rename would touch the row the job reads - the same
/// reason <see cref="WorkingHoursRule"/> is not a list inside <see cref="Worker"/> today.
///
/// <para><b>One worker, one schedule, one kind at a time.</b> <see cref="ScheduleKind.Weekly"/> means
/// "read the existing <see cref="WorkingHoursRule"/> rows the way `20-02` always did" - this aggregate
/// carries none of those hours itself, only the fact that weekly is the active kind plus the three
/// numbers every kind needs (<see cref="SlotMinutes"/>, <see cref="BufferMinutes"/>,
/// <see cref="HorizonDays"/>) and the cursor. <see cref="ScheduleKind.Cycle"/> additionally carries
/// <see cref="CycleAnchor"/>/<see cref="CycleWorkingDays"/>/<see cref="CycleRestDays"/> (which day is
/// working - see <see cref="CycleGrid"/>) and <see cref="CycleStartsAt"/>/<see cref="CycleEndsAt"/>
/// (the one wall-clock window applied to every working day the cycle produces). Switching kind clears
/// the other kind's own parameters on this aggregate - <see cref="ReconfigureWeekly"/> and
/// <see cref="ReconfigureCycle"/> each null out the fields the other kind uses, so a schedule never
/// carries stale cycle numbers behind a <c>Weekly</c> flag or vice versa.</para>
///
/// <para><b>The cycle chooses which days, never the hours inside a day.</b> That is what dissolves
/// the midnight problem `WorkingHoursRule`'s own doc comment refuses to solve: "сутки через трое" is
/// <c>1</c> working day in <c>4</c> plus the worker's ordinary daytime hours, not a 24-hour bookable
/// window. See <see cref="CycleGrid"/> and <see cref="IsCycleWorkingDay"/>.</para>
///
/// <para><b><see cref="MaterializeFrom"/> only ever moves forward, and the aggregate refuses to move
/// it backwards itself.</b> That is a Domain invariant rather than an Application-level check, for the
/// same reason <see cref="BookingCalendar"/> bounds its own buffer inside <c>Reconfigure</c> rather
/// than leaving the caller to remember: the guarantee belongs to the state it protects, so every
/// caller gets it for free, including a future one that forgets to check. Moving the cursor backwards
/// on purpose - a destructive re-cut - is deliberately `20-16`'s own entry point, not reachable from
/// here.</para>
/// </summary>
public sealed class WorkerSchedule
{
    /// <summary>Above this, a tenant who typed "3650" would have the job cut ten years of slots per
    /// worker on its very next run. Arbitrary in the same way <see cref="BookingCalendar"/>'s old
    /// buffer cap was: its job is to reject a fat-finger configuration at the door, not to encode a
    /// business rule.</summary>
    public const int MaxHorizonDays = 180;

    /// <summary>What a newly created schedule's horizon defaults to when a caller does not name one -
    /// the number that used to live on <c>AvailabilityMaterializationJobOptions.HorizonDays</c> before
    /// the horizon moved from "one value for the whole deployment" to "one value per worker". Still
    /// unmeasured, for the same honestly-stated reason that field's own remarks gave.</summary>
    public const int DefaultHorizonDays = 30;

    /// <summary>Same bound <see cref="BookingCalendar.BufferMinutes"/> used to carry, inherited
    /// unchanged now that the field itself has moved here: a buffer of a day or more would consume
    /// every slot it is supposed to separate.</summary>
    public const int MaxBufferMinutes = 8 * 60;

    public WorkerScheduleId Id { get; }

    public WorkerId WorkerId { get; }

    public ScheduleKind Kind { get; private set; }

    /// <summary>The first working day of the cycle - populated only while <see cref="Kind"/> is
    /// <see cref="ScheduleKind.Cycle"/>.</summary>
    public DateOnly? CycleAnchor { get; private set; }

    public int? CycleWorkingDays { get; private set; }

    public int? CycleRestDays { get; private set; }

    /// <summary>Wall-clock opening time, in the worker's calendar's own zone - the same wall-clock
    /// convention <see cref="WorkingHoursRule.StartsAt"/> uses, and for the same reason.</summary>
    public TimeOnly? CycleStartsAt { get; private set; }

    public TimeOnly? CycleEndsAt { get; private set; }

    /// <summary>How long one bookable slot is. Authoritative now that it is a number the tenant set
    /// rather than one derived from the longest offered service - <see cref="MaterializeAvailability"/>'s
    /// own remarks say what that costs a service that no longer fits.</summary>
    public int SlotMinutes { get; private set; }

    /// <summary>Dead time between consecutive slots - moved here from
    /// <see cref="BookingCalendar.BufferMinutes"/>, unchanged in meaning: zero means back-to-back, and
    /// this is not a lunch break (a hole in the middle of a day is two weekly rules, not a buffer).</summary>
    public int BufferMinutes { get; private set; }

    /// <summary>How many business-local days past today this worker's slots are kept generated.</summary>
    public int HorizonDays { get; private set; }

    /// <summary>The forward-only cursor the materialisation job reads and advances. The job
    /// materialises from <c>max(today, MaterializeFrom)</c> out to <c>today + HorizonDays</c>, then
    /// moves this past what it cut - see <c>MaterializeAvailabilityHandler</c>.</summary>
    public DateOnly MaterializeFrom { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private WorkerSchedule(
        WorkerScheduleId id, WorkerId workerId, ScheduleKind kind,
        int slotMinutes, int bufferMinutes, int horizonDays, DateOnly materializeFrom, DateTimeOffset now)
    {
        Id = id;
        WorkerId = workerId;
        Kind = kind;
        SlotMinutes = slotMinutes;
        BufferMinutes = bufferMinutes;
        HorizonDays = horizonDays;
        MaterializeFrom = materializeFrom;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private WorkerSchedule()
    {
    }

    public static WorkerSchedule CreateWeekly(
        WorkerScheduleId id, WorkerId workerId,
        int slotMinutes, int bufferMinutes, int horizonDays, DateOnly materializeFrom, DateTimeOffset now)
    {
        ValidateCommon(slotMinutes, bufferMinutes, horizonDays);
        return new WorkerSchedule(
            id, workerId, ScheduleKind.Weekly, slotMinutes, bufferMinutes, horizonDays, materializeFrom, now);
    }

    public static WorkerSchedule CreateCycle(
        WorkerScheduleId id, WorkerId workerId,
        DateOnly anchor, int workingDays, int restDays, TimeOnly startsAt, TimeOnly endsAt,
        int slotMinutes, int bufferMinutes, int horizonDays, DateOnly materializeFrom, DateTimeOffset now)
    {
        ValidateCommon(slotMinutes, bufferMinutes, horizonDays);
        ValidateCycle(workingDays, restDays, startsAt, endsAt);

        var schedule = new WorkerSchedule(
            id, workerId, ScheduleKind.Cycle, slotMinutes, bufferMinutes, horizonDays, materializeFrom, now);
        schedule.CycleAnchor = anchor;
        schedule.CycleWorkingDays = workingDays;
        schedule.CycleRestDays = restDays;
        schedule.CycleStartsAt = startsAt;
        schedule.CycleEndsAt = endsAt;
        return schedule;
    }

    /// <summary>Switches to (or stays on) <see cref="ScheduleKind.Weekly"/>, clearing every cycle
    /// field - so a schedule that has been Weekly since or switched back to Weekly never carries a
    /// previous cycle's numbers behind the flag.</summary>
    public void ReconfigureWeekly(
        int slotMinutes, int bufferMinutes, int horizonDays, DateOnly materializeFrom, DateTimeOffset now)
    {
        ValidateCommon(slotMinutes, bufferMinutes, horizonDays);
        ValidateMaterializeFromNotRegressing(materializeFrom);

        Kind = ScheduleKind.Weekly;
        CycleAnchor = null;
        CycleWorkingDays = null;
        CycleRestDays = null;
        CycleStartsAt = null;
        CycleEndsAt = null;

        SlotMinutes = slotMinutes;
        BufferMinutes = bufferMinutes;
        HorizonDays = horizonDays;
        MaterializeFrom = materializeFrom;
        UpdatedAt = now;
    }

    public void ReconfigureCycle(
        DateOnly anchor, int workingDays, int restDays, TimeOnly startsAt, TimeOnly endsAt,
        int slotMinutes, int bufferMinutes, int horizonDays, DateOnly materializeFrom, DateTimeOffset now)
    {
        ValidateCommon(slotMinutes, bufferMinutes, horizonDays);
        ValidateCycle(workingDays, restDays, startsAt, endsAt);
        ValidateMaterializeFromNotRegressing(materializeFrom);

        Kind = ScheduleKind.Cycle;
        CycleAnchor = anchor;
        CycleWorkingDays = workingDays;
        CycleRestDays = restDays;
        CycleStartsAt = startsAt;
        CycleEndsAt = endsAt;

        SlotMinutes = slotMinutes;
        BufferMinutes = bufferMinutes;
        HorizonDays = horizonDays;
        MaterializeFrom = materializeFrom;
        UpdatedAt = now;
    }

    /// <summary>
    /// The materialisation job's own write, after a successful run: past what it just cut, never
    /// earlier. <see cref="ValidateMaterializeFromNotRegressing"/> is the same guard the console's own
    /// save goes through - one rule, two callers, and neither can bypass it because it lives on the
    /// aggregate rather than in either caller.
    /// </summary>
    public void AdvanceCursor(DateOnly newCursor, DateTimeOffset now)
    {
        ValidateMaterializeFromNotRegressing(newCursor);
        MaterializeFrom = newCursor;
        UpdatedAt = now;
    }

    /// <summary>Whether <paramref name="day"/> is a working day under this schedule's cycle. Valid
    /// only while <see cref="Kind"/> is <see cref="ScheduleKind.Cycle"/> - a caller branches on
    /// <see cref="Kind"/> first, the same way it would have to branch to read
    /// <see cref="CycleStartsAt"/> at all.</summary>
    public bool IsCycleWorkingDay(DateOnly day)
    {
        if (Kind != ScheduleKind.Cycle)
        {
            throw new InvalidOperationException(
                $"Schedule {Id.Value} is not a cycle schedule (kind is {Kind}).");
        }

        return CycleGrid.IsWorkingDay(CycleAnchor!.Value, CycleWorkingDays!.Value, CycleRestDays!.Value, day);
    }

    private static void ValidateCommon(int slotMinutes, int bufferMinutes, int horizonDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferMinutes, MaxBufferMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(horizonDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(horizonDays, MaxHorizonDays);
    }

    private static void ValidateCycle(int workingDays, int restDays, TimeOnly startsAt, TimeOnly endsAt)
    {
        if (workingDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingDays), workingDays, "A cycle needs at least one working day.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(restDays);

        if (endsAt <= startsAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt), endsAt,
                $"A cycle's working hours must end after they start; got {startsAt:HH\\:mm} .. {endsAt:HH\\:mm}. " +
                "The cycle chooses working days, not hours crossing midnight - see WorkingHoursRule.For " +
                "for the same rule at the weekly kind.");
        }
    }

    /// <summary>The whole forward-only guarantee, in one place. Called with no prior value only from
    /// the two <c>Create*</c> factories, which is why they do not call this - there is no "current"
    /// value to regress from on first creation.</summary>
    private void ValidateMaterializeFromNotRegressing(DateOnly requested)
    {
        if (requested < MaterializeFrom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested), requested,
                $"MaterializeFrom cannot move backwards: schedule {Id.Value} is already at {MaterializeFrom:yyyy-MM-dd}. " +
                "Moving it earlier is a destructive re-cut and is deliberately not reachable from this save.");
        }
    }
}
