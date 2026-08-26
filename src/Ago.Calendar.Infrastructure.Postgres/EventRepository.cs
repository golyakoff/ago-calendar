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

    public async Task<IReadOnlySet<DateOnly>> ListMaterializedLocalDatesAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // No status filter, deliberately - see IEventRepository for why a cancelled row is still
        // evidence that a day was generated, even though it is not evidence that the worker's time
        // is occupied. `SELECT DISTINCT local_date` over ix_events_worker_day: an index-only scan of
        // one worker's window, returning at most one row per day rather than one per slot.
        var dates = await db.Events
            .Where(e => e.CalendarId == calendarId
                && e.WorkerId == workerId
                && e.LocalDate >= from
                && e.LocalDate <= to)
            .Select(e => e.LocalDate)
            .Distinct()
            .ToListAsync(cancellationToken);

        return dates.ToHashSet();
    }

    public async Task<int> InsertAvailableSlotsAsync(
        IReadOnlyCollection<Event> slots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            return 0;
        }

        // Raw SQL, and the one place in this product that needs to be, because the statement below
        // is the whole idempotency argument and EF Core has no way to express it: ON CONFLICT DO
        // NOTHING with no conflict target, which in Postgres covers *every* usable constraint on the
        // table - the primary key and, crucially, the GiST exclusion constraint. A row that would
        // overlap an existing event is dropped from the batch instead of aborting it, so two Worker
        // replicas generating the same day both succeed and exactly one set of rows lands. The EF
        // alternative (AddRange + SaveChanges) turns the loser of that race into a rolled-back
        // transaction and an exception the caller has to interpret, which is control flow by
        // exception for an outcome that is not exceptional. Same precedent as ago-chat's
        // PartitionMaintenanceJob: imperative SQL for the one statement whose exact text is the
        // guarantee.
        //
        // unnest() rather than a generated multi-row VALUES list: nine array parameters regardless
        // of batch size, so the statement text is constant and Postgres can reuse its plan, and
        // there is no parameter-count ceiling to trip over on a wide horizon.
        const string sql =
            """
            INSERT INTO events (id, tenant_id, calendar_id, worker_id, starts_at, ends_at, local_date, status, created_at)
            SELECT * FROM unnest(
                @ids::uuid[], @tenants::uuid[], @calendars::uuid[], @workers::uuid[],
                @starts::timestamptz[], @ends::timestamptz[], @dates::date[],
                @statuses::text[], @created::timestamptz[])
            ON CONFLICT DO NOTHING
            """;

        var ordered = slots.ToList();
        var parameters = new NpgsqlParameter[]
        {
            new("ids", ordered.ConvertAll(e => e.Id.Value).ToArray()),
            new("tenants", ordered.ConvertAll(e => e.TenantId.Value).ToArray()),
            new("calendars", ordered.ConvertAll(e => e.CalendarId.Value).ToArray()),
            new("workers", ordered.ConvertAll(e => e.WorkerId.Value).ToArray()),
            new("starts", ordered.ConvertAll(e => e.StartsAt).ToArray()),
            new("ends", ordered.ConvertAll(e => e.EndsAt).ToArray()),
            new("dates", ordered.ConvertAll(e => e.LocalDate).ToArray()),
            new("statuses", ordered.ConvertAll(e => e.Status.ToString()).ToArray()),
            new("created", ordered.ConvertAll(e => e.CreatedAt).ToArray()),
        };

        return await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    public async Task<IReadOnlyList<Event>> ListForDayAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, CancellationToken cancellationToken) =>
        await db.Events
            .Where(e => e.CalendarId == calendarId && e.WorkerId == workerId && e.LocalDate == localDate)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(cancellationToken);

    public async Task ReplaceDayAsync(
        CalendarId calendarId,
        WorkerId workerId,
        DateOnly localDate,
        IReadOnlyCollection<Event> replacements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacements);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The status filter is the safety property, not a convenience: a claimed row is not
        // addressable by this statement, so no manual edit can delete a booking - not through a
        // caller's bug, and not through a caller whose pre-read was overtaken by a customer. See
        // IEventRepository.ReplaceDayAsync.
        //
        // ExecuteDeleteAsync issues one DELETE and does not load the rows first; loading them to
        // delete them would be a read-then-write over the very set that is racing.
        await db.Events
            .Where(e => e.CalendarId == calendarId
                && e.WorkerId == workerId
                && e.LocalDate == localDate
                && (e.Status == EventStatus.Available || e.Status == EventStatus.Blocked))
            .ExecuteDeleteAsync(cancellationToken);

        if (replacements.Count > 0)
        {
            db.Events.AddRange(replacements);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsOverlapViolation(exception))
            {
                // A claimed row survived the delete and the replacement lands on top of it. Nothing
                // partial is left behind: the rollback below takes the delete with it, which is why
                // the two statements are in one explicit transaction rather than two SaveChanges.
                db.ChangeTracker.Clear();
                await transaction.RollbackAsync(cancellationToken);

                var first = replacements.Min(e => e.StartsAt);
                var last = replacements.Max(e => e.EndsAt);
                throw new SlotOverlapException(workerId, new TimeSlot(first, last), exception);
            }
        }

        await transaction.CommitAsync(cancellationToken);
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
