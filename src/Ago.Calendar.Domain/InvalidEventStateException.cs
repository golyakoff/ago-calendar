namespace Ago.Calendar.Domain;

/// <summary>
/// A transition the <see cref="Event"/> state machine does not allow - the same class of invariant
/// failure <c>Ago.Chat.Domain.InvalidConversationStateException</c> already names for its own
/// aggregate. A bug in the caller, never an expected outcome, so it is an exception rather than a
/// <c>Result</c> (coding-style.md).
///
/// <para><b>Not the same thing as losing a race.</b> Two customers reaching for one free slot is an
/// ordinary, expected outcome under concurrency, and the loser must not learn about it through this
/// type - it would be indistinguishable from a genuine caller bug. That path is the storage-level
/// compare-and-set and the row's own <c>xmin</c>; see <see cref="Event.Claim"/>.</para>
/// </summary>
public sealed class InvalidEventStateException(string message) : InvalidOperationException(message);
