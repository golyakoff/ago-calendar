namespace Ago.Calendar.Domain;

/// <summary>
/// Which step a <see cref="ChatBookingTask"/> most recently sent and is therefore waiting a reply
/// for. Named for what is being awaited rather than for what was just sent, because those are the
/// same fact stated from two directions and "awaiting" is the one <see cref="ChatBookingTask"/>'s own
/// caller (`ReplyToModuleTaskHandler`) actually branches on.
/// </summary>
public enum ChatBookingTaskState
{
    /// <summary>The task exists and the service <c>choice_list</c> has been sent.</summary>
    AwaitingServiceChoice,

    /// <summary>A service was chosen; the worker <c>choice_list</c> has been sent. Unconditional -
    /// this step exists whether the calendar has one worker or several (`25-33`'s own instruction:
    /// its placement here, before the date/time picker, is deliberate and unchanged, not a gap this
    /// item fills).</summary>
    AwaitingWorkerChoice,

    /// <summary>`25-33`: a worker was chosen; the <c>date_time_picker</c>'s <b>date round</b> has been
    /// sent - a calendar of upcoming dates that actually have availability, not a flat slot list. The
    /// four-primitive vocabulary is closed (`adr/0065` §4), so a real date-then-time flow is two
    /// rounds of the same <c>date_time_picker</c> kind rather than a fifth kind - this state is the
    /// first round; <see cref="AwaitingSlotChoice"/> is the second.</summary>
    AwaitingDateChoice,

    /// <summary>`25-33`: a date was chosen; the <c>date_time_picker</c>'s <b>time round</b>, scoped to
    /// that one date, has been sent. Also the state a failed booking attempt returns to, with a
    /// freshly re-queried slot list for the *same* date - see
    /// <see cref="ChatBookingTask.ReopenForSlotChoice"/> and <see cref="ChatBookingTask.SelectedDate"/>.
    /// Before `25-33` this state's own slot list spanned every day at once; it is now always scoped to
    /// one date, which is exactly what makes it and <see cref="AwaitingDateChoice"/> share one wire
    /// kind safely - see <see cref="ChatBookingTask.LastAppliedValue"/>'s own remarks on why the two
    /// adjacent same-kind states cannot be confused for each other the way `25-32`'s bug confused two
    /// adjacent same-kind choice-list states.</summary>
    AwaitingSlotChoice,

    /// <summary>A slot was chosen; the phone <c>form</c> has been sent.</summary>
    AwaitingPhone,

    /// <summary>The booking succeeded and a <c>confirmation_card</c> was sent. Terminal - no further
    /// reply is expected, and <see cref="ChatBookingTask"/> refuses one.</summary>
    Completed,
}
