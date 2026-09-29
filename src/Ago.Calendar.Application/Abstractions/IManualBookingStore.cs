using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-268`/`adr/0188`: an operator enters a booking that was already taken by phone - a run claimed
/// straight into <see cref="EventStatus.Booked"/> (the operator is the confirmation authority; there
/// is no veto window to skip through, only one that never opens), an upserted
/// <see cref="PersonRecord"/> - freshly minted, or an existing one the operator chose to reuse
/// (`26-268`§2a/`adr/0188`) - and either one or two outbox rows (<c>BookingConfirmed</c> always,
/// <c>PersonRegistered</c> only on a fresh mint), all in one transaction.
///
/// <para><b>Why a third port, not a flag on <see cref="IBookingStore"/> or
/// <see cref="IBookingRescheduleStore"/>.</b> Each of those two already earned its own port for the
/// identical reason: a distinct transactional shape stages a distinct set of outbox events, and
/// generalising with a <c>targetStatus</c>/optional-deadline parameter would couple the hot, contended
/// public path (<see cref="IBookingStore"/>) or the cancel-and-claim reschedule
/// (<see cref="IBookingRescheduleStore"/>) to a third caller with neither its concurrency profile nor
/// its own event set. This port's own shape is closest to <see cref="IBookingRescheduleStore"/>'s new-run
/// half - claim straight to <see cref="EventStatus.Booked"/>, no deadline - but it upserts a person
/// record the way <see cref="IBookingStore"/> does (a fresh mint, or - since `26-268`§2a/`adr/0188` - an
/// existing one the operator explicitly chose to reuse) rather than only ever carrying one forward, and
/// it stages <c>BookingConfirmed</c> (plus <c>PersonRegistered</c> on a fresh mint) rather than one
/// <c>BookingRescheduled</c>. No existing port's transaction shape is this one's, so - the same "the
/// transaction has to belong to something" reasoning both existing ports already state - it gets its
/// own.</para>
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
    /// Upserts the person record (freshly minted, or an existing one being reused) and attempts the
    /// claim, staging <c>BookingConfirmed</c> - and, on a fresh mint only, <c>PersonRegistered</c> - on
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
/// <param name="PersonId">The id <c>EnterManualBookingHandler</c> resolved for this attempt - either
/// freshly minted (`IIdGenerator.NewId`) or, since `26-268`§2a/`adr/0188`'s phone-based recognition, an
/// existing person the operator chose to reuse. Either way the person-record upsert this port performs
/// is idempotent on this id: an insert for a fresh mint, an ordinary re-confirm (phone, last-seen,
/// operator-confirmed-at) for a reuse - see <see cref="PersonRegisteredEvent"/>'s own remarks for the one
/// thing that differs between the two.</param>
/// <param name="Now">The instant the entry is happening, from <c>IClock</c>. Also the claim's "this slot
/// has not started yet" predicate and the person record's <c>first_seen_at</c>/<c>last_seen_at</c>.</param>
/// <param name="PersonRegisteredEvent">The <c>PersonRegistered</c> envelope, built by the handler
/// (Application - clean-architecture.md's own placement for the domain-to-contract mapping) from the
/// minted id and the name the operator typed - or <see langword="null"/> when this attempt reuses an
/// existing person (`26-268`§2a/`adr/0188`). A reused person already has a chat-side registration from
/// whichever earlier booking created <see cref="PersonId"/>'s own record; announcing it again would tell
/// chat to create a second registration for an id it already knows, which is not what happened here -
/// the operator recognised an existing client, they did not create one. Staged by the adapter inside the
/// transaction, on the success path only, and only when not <see langword="null"/> - a minted id that was
/// never claimed must never reach chat (CLAUDE.md rule 4).</param>
/// <param name="BookingConfirmedEvent">The <c>BookingConfirmed</c> envelope for the run's own anchor,
/// built by the handler from the target slot and the run's own end - deliberately not
/// <c>BookingPendingStateChanged</c>, because this booking was never pending (§3.5). Staged
/// unconditionally, on both the mint and the reuse path: either way a real booking was made.</param>
public readonly record struct ManualBookingAttempt(
    TenantId TenantId,
    CalendarId CalendarId,
    IReadOnlyList<EventId> EventIds,
    ServiceId ServiceId,
    PhoneNumber Phone,
    Guid PersonId,
    DateTimeOffset Now,
    EventEnvelope? PersonRegisteredEvent,
    EventEnvelope BookingConfirmedEvent);
