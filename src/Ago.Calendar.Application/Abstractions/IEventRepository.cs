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
/// make either shape safe when it arrives; see <see cref="SaveAsync"/>.</para>
///
/// <para>There is also no availability query. "Which slots are free on this day" is a read model
/// returning DTOs, not aggregates (adr/0004), and it belongs to `20-02` together with the Dapper
/// read store that serves it.</para>
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
    /// How far ahead this worker is already materialised in this calendar: the latest
    /// <see cref="Event.EndsAt"/> among rows that still occupy the worker's time. Null when nothing
    /// has been materialised yet.
    ///
    /// <para>This is `20-02`'s "where do I resume from" question, and it is a port method rather
    /// than a general "list this worker's events" because the answer is one instant - returning the
    /// rows to let the caller compute a maximum would drag a horizon's worth of aggregates across
    /// the wire to read one column.</para>
    /// </summary>
    Task<DateTimeOffset?> GetMaterializedHorizonAsync(
        CalendarId calendarId, WorkerId workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves one event's state transition. Throws <see cref="EventConcurrencyConflictException"/>,
    /// never a raw ORM exception, when another writer changed the row first - which under this
    /// product's central race (two customers, one slot) is the ordinary outcome for one of them.
    /// </summary>
    Task SaveAsync(Event @event, CancellationToken cancellationToken);
}
