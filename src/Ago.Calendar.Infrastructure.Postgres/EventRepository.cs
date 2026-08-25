using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// The adapter for <see cref="IEventRepository"/>, and the one place in this product allowed to know
/// that the storage engine is Postgres and the ORM is EF Core. Both translations below exist for
/// that reason: a handler that caught <c>PostgresException</c> would be a handler with an opinion
/// about the database.
/// </summary>
public sealed class EventRepository(AgoCalendarDbContext db) : IEventRepository
{
    public Task<Event?> GetByIdAsync(EventId id, CancellationToken cancellationToken) =>
        db.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task AddRangeAsync(IReadOnlyCollection<Event> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        db.Events.AddRange(events);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsOverlapViolation(exception))
        {
            // The whole batch rolled back, and the change tracker still holds every row of it as a
            // pending insert. Left alone, the caller's next SaveChangesAsync on this context would
            // retry the failed batch by accident - the same trap `6-08` documented for ago-chat's
            // conversation retry path.
            db.ChangeTracker.Clear();

            // Which row collided is not knowable from the constraint violation itself, so the
            // exception names the batch's own span rather than inventing a precise culprit. `20-02`
            // resumes from GetMaterializedHorizonAsync, which makes "somebody already materialised
            // part of this window" a resumable condition rather than a mystery.
            var first = events.Min(e => e.StartsAt);
            var last = events.Max(e => e.EndsAt);
            throw new SlotOverlapException(events.First().WorkerId, new TimeSlot(first, last), exception);
        }
    }

    public async Task<DateTimeOffset?> GetMaterializedHorizonAsync(
        CalendarId calendarId, WorkerId workerId, CancellationToken cancellationToken)
    {
        // Cancelled rows are excluded for the same reason the no-overlap constraint excludes them:
        // a cancelled event does not occupy the worker, so it is not evidence that the horizon
        // reaches that far. Without the filter, one cancellation at the far end of a window would
        // convince the materialiser it had nothing left to generate.
        var horizons = db.Events
            .Where(e => e.CalendarId == calendarId
                && e.WorkerId == workerId
                && e.Status != EventStatus.Cancelled)
            .Select(e => (DateTimeOffset?)e.EndsAt);

        return await horizons.MaxAsync(cancellationToken);
    }

    public async Task SaveAsync(Event @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (db.Entry(@event).State == EntityState.Detached)
        {
            db.Events.Add(@event);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A failed SaveChangesAsync untracks nothing: this event stays in the change tracker
            // holding an edit that never committed, so a caller that reloads on this same context
            // would hit the identity map and get its own stale copy back - silently defeating the
            // reload-and-retry it just decided to do.
            db.ChangeTracker.Clear();
            throw new EventConcurrencyConflictException(@event.Id);
        }
        catch (DbUpdateException exception) when (IsOverlapViolation(exception))
        {
            db.ChangeTracker.Clear();
            throw new SlotOverlapException(@event.WorkerId, @event.Slot, exception);
        }
    }

    /// <summary><c>23P01 exclusion_violation</c> - the GiST constraint refused the write. Matched on
    /// the SQLSTATE rather than on the constraint's name: the code is the contract Postgres
    /// documents, a name is a string a future migration could rename.</summary>
    private static bool IsOverlapViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation };
}
