using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-268`/`adr/0188`: an operator enters a booking that was already taken by phone - a run claimed
/// straight into <see cref="EventStatus.Booked"/> (the operator is the confirmation authority; there
/// is no veto window to skip through, only one that never opens), a freshly minted
/// <see cref="PersonRecord"/>, and two outbox rows (<c>PersonRegistered</c>, <c>BookingConfirmed</c>),
/// all in one transaction.
///
/// <para><b>Why a third port, not a flag on <see cref="IBookingStore"/> or
/// <see cref="IBookingRescheduleStore"/>.</b> Each of those two already earned its own port for the
/// identical reason: a distinct transactional shape stages a distinct set of outbox events, and
/// generalising with a <c>targetStatus</c>/optional-deadline parameter would couple the hot, contended
/// public path (<see cref="IBookingStore"/>) or the cancel-and-claim reschedule
/// (<see cref="IBookingRescheduleStore"/>) to a third caller with neither its concurrency profile nor
/// its own event set. This port's own shape is closest to <see cref="IBookingRescheduleStore"/>'s new-run
/// half - claim straight to <see cref="EventStatus.Booked"/>, no deadline - but it upserts a
/// <em>fresh</em> person (like <see cref="IBookingStore"/>) rather than carrying an existing one forward,
/// and it stages <c>PersonRegistered</c> + <c>BookingConfirmed</c> rather than one <c>BookingRescheduled</c>.
/// No existing port's transaction shape is this one's, so - the same "the transaction has to belong to
/// something" reasoning both existing ports already state - it gets its own.</para>
///
/// <para><b>Rule 8 holds.</b> The run's availability is decided by the claim's own
/// <c>WHERE status = 'Available' AND starts_at &gt; @now</c> inside the transaction - never a pre-read,
/// never cached. <c>ConsecutiveRunFinder</c>'s courtesy read in <c>EnterManualBookingHandler</c> only
/// decides <i>which ids to ask the claim for</i>; a stale answer there costs nothing more than the
/// ordinary "slot no longer available" outcome below.</para>
/// </summary>
public interface IManualBookingStore
{
    /// <summary>
    /// Upserts the freshly minted person record and attempts the claim, staging both outbox events on
    /// the success path only.
    ///
    /// <para>Returns <see langword="null"/> when the run was not claimable in full - any one of its
    /// slots was taken, blocked, already started, or not on the named calendar by the time this
    /// statement ran. <b>That is an ordinary outcome, not a fault</b> - the identical posture
    /// <see cref="IBookingStore.TryBookAsync"/> and <see cref="IBookingRescheduleStore.TryRescheduleAsync"/>
    /// both take for their own lost race: the whole transaction rolls back, nothing is written, and
    /// nothing is staged. Never logged at <c>Error</c>, never a 500.</para>
    /// </summary>
    Task<BookingConfirmation?> TryEnterAsync(ManualBookingAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>
/// Everything one manual-entry write needs, resolved by <c>EnterManualBookingHandler</c> before the
/// transaction opens - the same "a record rather than a long parameter list" shape
/// <see cref="BookingAttempt"/> and <see cref="BookingRescheduleRequest"/> both use.
/// </summary>
/// <param name="CalendarId">Part of the claim's own <c>WHERE</c> clause, not a pre-check - the same
/// "unclaimable by construction" reasoning <see cref="BookingAttempt.CalendarId"/> states.</param>
/// <param name="EventIds">The run being claimed, in start order - <see cref="ConsecutiveRunFinder.FindRun"/>'s
/// own contract. <see cref="EventIds"/>[0] is the anchor, exactly as <see cref="BookingAttempt.EventIds"/>'s
/// own remarks state.</param>
/// <param name="Phone">Snapshotted onto the freshly minted person record - the operator's own "I called
/// and it is them" fact, recorded as <see cref="PersonRecord.PhoneConfirmedByOperatorAt"/>
/// (`23-12`/§3.3), never as <see cref="PersonRecord.PhoneVerifiedAt"/>, which stays null: this person has
/// not proven reachability by SMS.</param>
/// <param name="PersonId">The id <c>EnterManualBookingHandler</c> minted for this attempt
/// (`IIdGenerator.NewId`) - always freshly minted in this slice (§3.4's reuse-by-phone recognition is
/// deferred), so the person-record upsert this port performs is, in production traffic, always an
/// insert.</param>
/// <param name="Now">The instant the entry is happening, from <c>IClock</c>. Also the claim's "this slot
/// has not started yet" predicate and the person record's <c>first_seen_at</c>/<c>last_seen_at</c>.</param>
/// <param name="PersonRegisteredEvent">The <c>PersonRegistered</c> envelope, built by the handler
/// (Application - clean-architecture.md's own placement for the domain-to-contract mapping) from the
/// minted id and the name the operator typed. Staged by the adapter inside the transaction, on the
/// success path only - a minted id that was never claimed must never reach chat (CLAUDE.md rule 4).</param>
/// <param name="BookingConfirmedEvent">The <c>BookingConfirmed</c> envelope for the run's own anchor,
/// built by the handler from the target slot and the run's own end - deliberately not
/// <c>BookingPendingStateChanged</c>, because this booking was never pending (§3.5).</param>
public readonly record struct ManualBookingAttempt(
    TenantId TenantId,
    CalendarId CalendarId,
    IReadOnlyList<EventId> EventIds,
    ServiceId ServiceId,
    PhoneNumber Phone,
    Guid PersonId,
    DateTimeOffset Now,
    EventEnvelope PersonRegisteredEvent,
    EventEnvelope BookingConfirmedEvent);
