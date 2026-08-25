using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// Another writer changed this event's row between the moment it was loaded and the moment the save
/// was attempted - the row's <c>xmin</c> rejected the write.
///
/// <para><b>Why the port raises this instead of letting EF's own exception through.</b> The
/// dependency rule (clean-architecture.md) is one reason: <c>DbUpdateConcurrencyException</c> lives
/// in <c>Microsoft.EntityFrameworkCore</c>, and a handler that catches it is a handler that knows
/// which ORM is underneath. The better reason is that this is the *expected* outcome of two
/// customers reaching for the same slot, not a fault - a caller has to be able to tell it apart from
/// a genuine bug, and "whatever exception the persistence library happened to throw" is not a
/// contract anyone can write a retry against. The same translation AGO Chat's
/// <c>ConversationConcurrencyConflictException</c> already does, for the same reason (`6-08`).</para>
///
/// <para>Distinct from <see cref="InvalidEventStateException"/>: that one means the caller asked for
/// a transition the state machine forbids, which is a bug. This one means the caller asked for a
/// legal transition and lost a race, which is Tuesday.</para>
/// </summary>
public sealed class EventConcurrencyConflictException(EventId eventId)
    : Exception($"Event {eventId.Value} changed since it was loaded.")
{
    public EventId EventId { get; } = eventId;
}
