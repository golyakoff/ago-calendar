namespace Ago.Calendar.Domain;

/// <summary>
/// A reply arrived for a step the task is not (or no longer) waiting on - the same class of invariant
/// failure <see cref="InvalidEventStateException"/> already names for <see cref="Event"/>. A bug in
/// the caller (`ReplyToModuleTaskHandler` sent the wrong transition, or two replies for one task
/// raced), never an expected outcome, so it is an exception rather than a <c>Result</c>
/// (coding-style.md).
/// </summary>
public sealed class InvalidChatBookingTaskStateException(string message) : InvalidOperationException(message);
