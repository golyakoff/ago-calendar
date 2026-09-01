namespace Ago.Calendar.Domain;

/// <summary>
/// The pure day-selection rule for a <see cref="ScheduleKind.Cycle"/> schedule: "N working days, then
/// M resting days, repeating forever from an anchor date". No state, no drift correction - the answer
/// for any day is computed fresh from the same four numbers every time, which is what lets the
/// materialiser ask it about a day a year from now with no different a cost than asking about
/// tomorrow.
///
/// <para><b>Deliberately just this one function, in Domain, with zero infrastructure dependency</b> -
/// the same reason <see cref="SlotGrid.Fill"/> lives here: this is arithmetic on <see cref="DateOnly"/>
/// values, so a unit test can assert "2/2 anchored on a Monday marks Monday and Tuesday working,
/// Wednesday and Thursday resting" without a database, a clock, or a time zone in sight - the cycle
/// chooses *days*, never wall-clock hours, which is exactly what keeps it independent of everything
/// <see cref="WorkingHoursRule"/> and <see cref="MaterializeAvailability"/> handle instead.</para>
///
/// <para><b>"Сутки через трое" needs no midnight-crossing rule of its own.</b> The cycle only ever
/// answers "is this business-local day a working one" - the hours worked *inside* a working day are
/// still an ordinary daytime window (<see cref="WorkerSchedule.CycleStartsAt"/>/
/// <see cref="WorkerSchedule.CycleEndsAt"/>), resolved the same way <see cref="WorkingHoursRule"/>'s
/// own hours are. A worker nominally "on duty overnight" still only sells the bookable hours the shop
/// states, so no 24-hour window is ever generated and nothing here has to reason about a shift that
/// crosses midnight.</para>
/// </summary>
public static class CycleGrid
{
    /// <summary>
    /// Whether <paramref name="day"/> falls inside the working portion of the cycle anchored on
    /// <paramref name="anchor"/>.
    ///
    /// <para><see cref="DateOnly.DayNumber"/> - the day's ordinal since 0001-01-01 - is what makes
    /// this a single subtraction and a modulo rather than a loop: no ambient calendar state, and a day
    /// before the anchor is handled by the same formula as a day after it, through the "add the
    /// cycle length before taking the remainder" trick that keeps the result in <c>[0, cycleLength)</c>
    /// even when the raw offset is negative.</para>
    /// </summary>
    /// <param name="anchor">The first working day of a cycle - position zero.</param>
    /// <param name="workingDays">How many consecutive days starting at the anchor (and at every
    /// repeat of the cycle) are working days. Must be at least one - a cycle with none would never
    /// answer "working" for anything, which is not a schedule, it is an absence of one.</param>
    /// <param name="restDays">How many consecutive days follow the working block before the cycle
    /// repeats. Zero is legal and means the worker is on duty every day.</param>
    /// <param name="day">The business-local day being asked about.</param>
    public static bool IsWorkingDay(DateOnly anchor, int workingDays, int restDays, DateOnly day)
    {
        if (workingDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingDays), workingDays, "A cycle needs at least one working day.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(restDays);

        var cycleLength = workingDays + restDays;
        var offset = day.DayNumber - anchor.DayNumber;

        // Proper (non-truncating) modulo: C#'s % can return a negative result for a negative
        // dividend, and a day before the anchor produces exactly that. Adding cycleLength before the
        // second % brings the result back into [0, cycleLength) without a branch.
        var position = ((offset % cycleLength) + cycleLength) % cycleLength;

        return position < workingDays;
    }
}
