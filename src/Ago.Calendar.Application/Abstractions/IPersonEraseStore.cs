using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-275`/`adr/0189`: the person-erasure write - a guarded delete of the <see cref="PersonRecord"/>
/// row, an anonymising update of every other event this person ever touched, and one staged
/// <c>PersonErased</c> outbox row, all in one transaction.
///
/// <para><b>Why a third port next to <see cref="IPersonRecordRepository"/> and
/// <see cref="ITenantErasureRepository"/>, not a method on either.</b> The identical reasoning
/// <see cref="IManualBookingStore"/>'s own remarks give for itself: a distinct transactional shape
/// stages a distinct outbox event, and <see cref="IPersonRecordRepository"/> is explicitly "what an
/// operator does to the record - read it, confirm a phone by calling" (its own remarks), never a
/// destructive multi-table write. <see cref="ITenantErasureRepository"/> is the identical shape one
/// scope wider - a whole tenant, every table - and reusing it for one person would mean building a
/// second, narrower erasure inside a method whose entire contract is "everything, by tenant id."</para>
///
/// <para><b>The guard is inside the same statement as the delete, not a courtesy pre-read.</b> CLAUDE.md
/// rule 8: "any compare-and-set read comes from the database inside the transaction." Whether this
/// person has a live future booking is exactly that kind of read - checked once, outside the write,
/// it could go stale in the gap before the write runs, the identical race
/// <see cref="Ago.Calendar.Infrastructure.Postgres.WorkerRepository.DeleteIfNeverBookedAsync"/> already
/// guards against for a worker's own booking history. The adapter's own <c>DELETE ... WHERE NOT EXISTS
/// (...)</c> is that same idiom, reused rather than reinvented.</para>
///
/// <para><b>Anonymise, never delete, the person's own past events</b> - the author's decision on issue
/// 1815: erasure must not trigger, or silently change the input to, any analytics recompute. Nulling
/// <c>events.person_id</c> leaves every aggregate-by-service/worker/date column untouched while
/// removing the one column that named this person, which is also what lets the guarded delete below
/// succeed at all - <c>events.person_id</c> carries a real foreign key to <c>person_records</c>
/// (<c>EventConfiguration</c>'s own remarks), so the row cannot be deleted while any event still points
/// at it. The anonymising update's own <c>WHERE</c> clause is written to never touch a live future
/// booking, regardless of when it was created - see the adapter's own remarks for why that, not a
/// separate before/after ordering, is what keeps this race-free.</para>
/// </summary>
public interface IPersonEraseStore
{
    /// <summary>
    /// Attempts the erasure. Returns <see langword="false"/> when the person could not be erased -
    /// either a live future booking still blocks it, or the row was already gone (a concurrent second
    /// erasure, or an id that stopped existing between the handler's own existence check and this
    /// call) - and the whole transaction rolls back: nothing is written, nothing is anonymised, nothing
    /// is staged. The caller (<c>ErasePersonHandler</c>) re-reads <see cref="IPersonRecordRepository"/>
    /// to tell those two outcomes apart, the identical "the guarded statement decides, a follow-up read
    /// only decides how to word the refusal" split <c>DeleteWorkerHandler</c>'s own remarks state.
    /// </summary>
    Task<bool> TryEraseAsync(PersonEraseAttempt attempt, CancellationToken cancellationToken);
}

/// <param name="TenantId">Scopes both the guard and the delete - the identical
/// "wrong tenant matches nothing" shape every tenant-scoped write in this product already takes.</param>
/// <param name="PersonId">Who is being erased.</param>
/// <param name="Now">The instant the future-bookings guard measures against - from <c>IClock</c>,
/// never a database-side <c>now()</c>, the same convention every other write in this product
/// follows.</param>
/// <param name="PersonErasedEvent">The outbox envelope built by the handler (Application), staged by
/// the adapter inside the transaction, on the success path only - a blocked or not-found attempt must
/// never tell chat a person was erased that was not (CLAUDE.md rule 4).</param>
public readonly record struct PersonEraseAttempt(
    TenantId TenantId,
    Guid PersonId,
    DateTimeOffset Now,
    EventEnvelope PersonErasedEvent);
