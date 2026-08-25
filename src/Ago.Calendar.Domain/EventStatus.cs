namespace Ago.Calendar.Domain;

/// <summary>
/// The one status column that makes a slot and a booking the same row - see <see cref="Event"/>'s
/// own remarks for why that is the design rather than two tables.
/// </summary>
public enum EventStatus
{
    /// <summary>Materialised from a <see cref="WorkingHoursRule"/> and bookable by anyone.</summary>
    Available,

    /// <summary>Claimed by a customer; the slot is gone, but an operator may still veto it until
    /// <see cref="Event.ConfirmationDeadline"/>.</summary>
    PendingConfirmation,

    /// <summary>Confirmed - by the deadline passing unchallenged (`20-04`'s sweep), or by an
    /// operator acting early.</summary>
    Booked,

    /// <summary>Withdrawn: an operator's veto inside the window, or an outright cancellation
    /// afterwards. The only status that frees the worker's time again.</summary>
    Cancelled,

    /// <summary>A past <see cref="Booked"/> visit the customer did not attend.</summary>
    NoShow,

    /// <summary>Time the worker is unavailable for a reason the schedule does not express - a
    /// break, a one-off closure. Never bookable; still occupies the worker.</summary>
    Blocked,
}
