namespace Ago.Calendar.Domain;

/// <summary>
/// The two shapes a <see cref="WorkerSchedule"/> can take - never both at once. See
/// <see cref="WorkerSchedule"/>'s own remarks for why mixing them is not offered.
/// </summary>
public enum ScheduleKind
{
    /// <summary>"An ordinary week" - the existing <see cref="WorkingHoursRule"/> rows, unchanged.
    /// The schedule itself carries none of the weekly hours; it only says which kind is active.</summary>
    Weekly = 0,

    /// <summary>N working days, M resting days, cycling from an anchor date - "2 через 2",
    /// "сутки через трое". See <see cref="CycleGrid"/> for the day-selection rule.</summary>
    Cycle = 1,
}
