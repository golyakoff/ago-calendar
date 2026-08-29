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

    /// <summary>A service was chosen; the worker <c>choice_list</c> has been sent.</summary>
    AwaitingWorkerChoice,

    /// <summary>A worker was chosen; the <c>date_time_picker</c> has been sent. Also the state a
    /// failed booking attempt returns to, with a freshly re-queried slot list - see
    /// <see cref="ChatBookingTask.ReopenForSlotChoice"/>.</summary>
    AwaitingSlotChoice,

    /// <summary>A slot was chosen; the phone <c>form</c> has been sent.</summary>
    AwaitingPhone,

    /// <summary>The booking succeeded and a <c>confirmation_card</c> was sent. Terminal - no further
    /// reply is expected, and <see cref="ChatBookingTask"/> refuses one.</summary>
    Completed,
}
