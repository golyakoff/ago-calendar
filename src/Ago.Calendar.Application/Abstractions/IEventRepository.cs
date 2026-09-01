using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Event"/> - the aggregate every other item in this stage
/// revolves around.
///
/// <para><b>What is deliberately not here.</b> There is no <c>TryClaimAsync</c>. `20-03`'s booking
/// claim is a single atomic <c>UPDATE ... WHERE status = 'Available'</c>, and the shape of that port
/// depends on decisions `20-03` has not made yet - whether the claim and the customer's
/// find-or-create share one transaction, and what the caller needs back when it loses. Declaring it
/// now would be guessing at a signature for the one operation in this product that must not be
/// guessed at. The <c>xmin</c> mapping and the exclusion constraint this item does ship are what
/// make either shape safe when it arrives; see <see cref="SaveAsync"/>. <b>`20-03` answered it</b>:
/// the claim is a raw <c>UPDATE ... WHERE status = 'Available'</c> sharing one transaction with the
/// customer upsert, so it went to <see cref="IBookingStore"/> rather than here, and this port never
/// grew the method - which is what refusing to guess a signature early bought.</para>
///
/// <para>There is also no availability query. "Which slots are free on this day" is a read model
/// returning DTOs, not aggregates (adr/0004), and it belongs with the Dapper read store that serves
/// the public booking page. `20-02` predicted `20-03` would be the first item with a caller for it;
/// it was not - `20-03` books a slot the caller already chose and never lists any, so the first real
/// caller is `20-06`'s booking widget. Recorded rather than quietly corrected, because a prediction
/// about who needs a port is the kind of thing that gets built on.</para>
///
/// <para>`20-03` did add the claim, and it is not here either: it is a compare-and-set spanning a
/// transaction with the lead-card upsert, so it lives on <see cref="IBookingStore"/> (adr/0059).
/// `20-01` guessed it would land on this port and was right to refuse to declare it early.</para>
/// </summary>
public interface IEventRepository
{
    Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a materialisation batch in one transaction. Throws <see cref="SlotOverlapException"/>
    /// if any row in the batch collides with an existing event for the same worker - including one
    /// written by a concurrent run of the same job, which is the case an in-memory check cannot see.
    /// </summary>
    Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken);

    /// <summary>
    /// Which business-local days in <c>[from, to]</c> already have at least one event row for this
    /// worker on this calendar - the materialiser's "what is already done" question, and the
    /// mechanism that makes re-running it non-destructive (adr/0053).
    ///
    /// <para><b>This replaced `20-01`'s <c>GetMaterializedHorizonAsync</c>, and the reason is worth
    /// stating.</b> That method answered "how far ahead does this worker's occupied time reach",
    /// as one instant. An instant can only describe a prefix, and the feature this item exists to
    /// build - a tenant editing one already-generated day - punches holes that a prefix cannot
    /// express. The unit the non-destructive rule operates in is the *day*, so the question the
    /// port asks has to be about days too.</para>
    ///
    /// <para><b>Every status counts, cancelled included</b> - which is the exact opposite of what
    /// the horizon method had to do, for a reason that inverts with the granularity. A cancelled row
    /// does not occupy the worker, so it was no evidence of how far the horizon reached; it is
    /// perfect evidence that the day was already generated. Counting it is what stops a day whose
    /// only booking was cancelled from being re-generated on top of its own history.</para>
    /// </summary>
    Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts freshly generated <see cref="EventStatus.Available"/> slots, skipping any that would
    /// collide with a row that already exists, and returns how many were actually written.
    ///
    /// <para><b>Idempotent by construction, not by convention.</b> The adapter issues a single
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c>, so a second materialisation run over the same
    /// window inserts nothing and two <c>Ago.Calendar.Worker</c> replicas racing the same day cannot
    /// both win: whichever loses the exclusion constraint has its rows dropped rather than its
    /// transaction aborted. No read precedes the write, so there is no check-then-act to lose
    /// (`6-09`, CLAUDE.md rule 8) - the day-set query above is an optimisation that keeps the common
    /// case from generating rows at all, never the guarantee.</para>
    ///
    /// <para>The returned count is what makes "the second run wrote nothing" an assertion rather
    /// than an inference, and it is what the job logs.</para>
    /// </summary>
    Task<int> InsertAvailableSlotsAsync(IReadOnlyCollection<Event> slots, CancellationToken cancellationToken);

    /// <summary>Every event on one business-local day for one worker, whatever its status - what a
    /// manual edit has to look at before it decides whether the day may be touched at all.</summary>
    Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces one business-local day's *unclaimed* rows with <paramref name="replacements"/>, in
    /// one transaction. The mechanism behind both manual edits (`20-02`): a day off is a replacement
    /// by a single <see cref="EventStatus.Blocked"/> row, a boundary change is a replacement by a
    /// freshly generated grid.
    ///
    /// <para><b>Only <see cref="EventStatus.Available"/> and <see cref="EventStatus.Blocked"/> rows
    /// are deleted, and that is the safety property, not a filter.</b> A row a customer has claimed
    /// is not addressable by this method at all, so no edit - and no bug in a caller - can delete a
    /// booking. The delete's own <c>WHERE</c> clause is the guard, which is what makes it safe under
    /// concurrency: a caller that read the day, found it clean, and was overtaken by a booking
    /// before it wrote does not delete that booking. Its replacements then overlap the surviving
    /// claimed row, the exclusion constraint refuses the whole transaction, and the caller gets
    /// <see cref="SlotOverlapException"/> instead of a silent loss. Reading first and trusting the
    /// read would be `6-09`'s defect with different nouns.</para>
    ///
    /// <para><see cref="EventStatus.Cancelled"/> rows are left strictly alone: they are the history
    /// of who cancelled on whom, which is exactly what a lead card exists to keep.</para>
    /// </summary>
    Task ReplaceDayAsync(
        CalendarId calendarId,
        WorkerId workerId,
        DateOnly localDate,
        IReadOnlyCollection<Event> replacements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves one event's state transition. Throws <see cref="EventConcurrencyConflictException"/>,
    /// never a raw ORM exception, when another writer changed the row first - which under this
    /// product's central race (two customers, one slot) is the ordinary outcome for one of them.
    /// </summary>
    Task SaveAsync(Event @event, CancellationToken cancellationToken);

    /// <summary>
    /// `20-18`: every row of one booking - the anchor plus every slot claimed alongside it - given
    /// any one member's own <see cref="Event.BookingId"/>. What
    /// <c>CancelBookingHandler</c>/<c>RejectBookingHandler</c>/<c>MarkNoShowHandler</c> load before
    /// acting: an operator's route names one event id, which may be any slot of the run, and this is
    /// the lookup that turns "one id" into "the whole booking" before a transition is attempted on any
    /// of it.
    /// </summary>
    Task<IReadOnlyList<Event>> ListByBookingIdAsync(EventId bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves every row of one booking's transition in a single round trip. Throws
    /// <see cref="EventConcurrencyConflictException"/> under the identical rule <see cref="SaveAsync"/>
    /// already states, generalised to a set: if any row of the run was changed first by another
    /// writer, none of this call's changes commit - a run's cancel, reject or no-show is one atomic
    /// act, never a partial one that leaves some slots transitioned and others not.
    /// </summary>
    Task SaveRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken);
}
