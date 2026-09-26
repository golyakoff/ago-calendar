using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-208`/`adr/0187`: the operator reschedule write, in one transaction - cancel the old run and
/// claim the new one, together or not at all.
///
/// <para><b>Why this is a port of its own, not a method on <see cref="IEventRepository"/> or
/// <see cref="IBookingStore"/>.</b> A reschedule spans two statements of different shapes that must
/// share one transaction: the <b>claim</b> of the new run is a contended, racy compare-and-set best
/// expressed as a raw atomic <c>UPDATE</c> whose rows-affected count is the verdict (the same reason
/// <see cref="IBookingStore"/>'s own remarks give for being raw), and the <b>cancel</b> of the old run
/// is an ordinary single-actor EF load-mutate-save protected by <c>xmin</c>. No existing port owns
/// both, and the transaction spanning them has to belong to something - the identical "the transaction
/// has to belong to something" reasoning <see cref="IBookingStore"/> gives for being one port rather
/// than two. This is also the exact precedent <c>ExpiredBookingConfirmer</c> already sets: a raw
/// Npgsql claim and an EF transition on one connection, one transaction. The alternative - splitting
/// the claim onto <see cref="IBookingStore"/> and the cancel onto <see cref="IEventRepository"/> - puts
/// the transaction boundary in a handler that cannot express it (Application must not open a database
/// transaction; that is Infrastructure's job, adr/0004), so a lost claim-race could leave the old
/// booking already cancelled - the precise torn state one transaction exists to forbid.</para>
///
/// <para><b>Rule 8 holds.</b> The new run's availability is a write decision, decided by the claim's
/// own <c>WHERE status = 'Available' AND starts_at &gt; @now</c> inside the transaction - never a
/// pre-read, never cached. <c>ConsecutiveRunFinder</c>'s courtesy read in the handler only decides
/// <i>which ids to ask the claim for</i>; a stale answer there costs nothing more than the ordinary
/// "slot no longer available" outcome below.</para>
/// </summary>
public interface IBookingRescheduleStore
{
    /// <summary>
    /// Claims the new run straight into <see cref="EventStatus.Booked"/> and cancels the old run, in
    /// one transaction, staging <see cref="Ago.Calendar.Contracts.BookingRescheduled"/> alongside them
    /// (rule 4).
    ///
    /// <para>Returns <see langword="false"/> when the new run was not claimable in full - any one of
    /// its slots was taken by somebody else moments ago, already started, blocked, or not on the named
    /// calendar. <b>That is an ordinary outcome, not a fault</b>, the same posture
    /// <see cref="IBookingStore.TryBookAsync"/> takes for its own lost race: the whole transaction
    /// rolls back, nothing is written, and - the property that matters most - <b>the old booking stays
    /// <see cref="EventStatus.Booked"/>, untouched</b>. Never logged at <c>Error</c>, never a 500.</para>
    ///
    /// <para>Throws <see cref="EventConcurrencyConflictException"/> if the old run was changed by
    /// another writer between the handler's read and this transaction (the cancel-half's <c>xmin</c>
    /// check), and <see cref="InvalidEventStateException"/> if the old run is no longer
    /// <see cref="EventStatus.Booked"/> by the time the cancel runs - both the same translations the
    /// other lifecycle writes surface, so the handler maps them to the identical failures.</para>
    /// </summary>
    Task<bool> TryRescheduleAsync(BookingRescheduleRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Everything one reschedule write needs, resolved by <c>RescheduleBookingHandler</c> before the
/// transaction opens. A record rather than a long parameter list, matching
/// <see cref="BookingAttempt"/>'s own shape.
/// </summary>
/// <param name="PreviousBookingId">The old run's anchor id (<c>Event.BookingId</c>) - the rows the
/// cancel-half loads and transitions to <see cref="EventStatus.Cancelled"/>.</param>
/// <param name="CalendarId">Part of the claim's own <c>WHERE</c> clause, not a pre-check - a slot on
/// another calendar is unclaimable by construction. The same calendar the old run is on (v1 is a
/// same-worker, same-service, time-only move).</param>
/// <param name="NewEventIds">The new run being claimed, in start order - one id for an ordinary
/// booking, several for a service that spans more than one slot. <see cref="NewEventIds"/>[0] is the
/// new anchor: the id every other new row's own <c>Event.BookingId</c> is set to, and the moved
/// booking's new identity. Computed server-side by <c>ConsecutiveRunFinder.FindRun</c>, never trusted
/// from the request.</param>
/// <param name="PersonId">`adr/0184`: the account-scoped person the old booking was for, carried
/// unchanged onto every claimed new row.</param>
/// <param name="ServiceId">The old booking's service, unchanged - v1 moves the time only, so the new
/// run's length and buffers are computed from the same service.</param>
/// <param name="OriginConversationId">The old booking's originating chat conversation, or
/// <see langword="null"/> - carried opaquely onto the new run (`adr/0184`).</param>
/// <param name="Now">The instant the reschedule is happening, from <c>IClock</c>. Also the claim's
/// "this slot has not started yet" predicate and the old run's cancellation time.</param>
/// <param name="RescheduledEvent">
/// The one <see cref="Ago.Calendar.Contracts.BookingRescheduled"/> envelope, built by the handler via
/// <c>BookingRescheduledMapper</c> from the two runs it already resolved. The store stages it onto the
/// outbox <b>only on the success path</b>, inside the transaction, so it commits with the cancel and
/// the claim (rule 4) and is never staged for a claim that lost the race. The mapping lives in the
/// handler (Application), where clean-architecture.md places the domain-to-contract translation,
/// leaving this store as pure transactional persistence.
/// </param>
public readonly record struct BookingRescheduleRequest(
    EventId PreviousBookingId,
    CalendarId CalendarId,
    IReadOnlyList<EventId> NewEventIds,
    Guid PersonId,
    ServiceId ServiceId,
    Guid? OriginConversationId,
    DateTimeOffset Now,
    EventEnvelope RescheduledEvent);
