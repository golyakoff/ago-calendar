using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// One tick's worth of the confirmation sweep, in one transaction: claim, confirm, stage, commit.
/// The reasoning for the shape - and why it is the same shape as <c>ConversationAssignmentJob</c> and
/// <c>OutboxDispatcher</c> rather than anything new - is on <see cref="IExpiredBookingConfirmer"/>.
///
/// <para>The claim is raw Npgsql on the <see cref="AgoCalendarDbContext"/>'s own connection, and the
/// transition is EF on the same connection inside the same transaction. That pairing is the whole
/// design: the row lock the claim takes is still held when <see cref="Event.Confirm"/> runs, so
/// nothing can move the row between the two, and both land or neither does. `4-02` reached the same
/// arrangement from the other direction - it refactored <c>OperatorCapacityStore</c> so a capacity
/// claim could share the assignment's transaction, for exactly this reason.</para>
/// </summary>
public sealed class ExpiredBookingConfirmer(
    AgoCalendarDbContext db,
    IOutboxWriter outbox,
    IIdGenerator idGenerator) : IExpiredBookingConfirmer
{
    /// <summary>
    /// <c>WaitingConversationClaimQuery</c>'s query shape, with this product's own predicate.
    ///
    /// <para><c>status = 'PendingConfirmation' AND confirmation_deadline &lt;= @now</c> is the whole
    /// decision, and it is made inside the statement that takes the lock - not read first and acted
    /// on afterwards. The same discipline `20-03`'s claim uses, and for the same reason: a predicate
    /// evaluated in application code is a predicate evaluated against a reading from milliseconds
    /// ago.</para>
    ///
    /// <para><c>ORDER BY confirmation_deadline</c>: oldest overdue first, so a backlog drains in the
    /// order it accumulated and no booking can be starved by newer ones arriving. <c>ix_events_pending_confirmation</c>
    /// (`20-01`) is a partial index on exactly <c>(tenant_id, confirmation_deadline)
    /// WHERE status = 'PendingConfirmation'</c> - written for this query before this query
    /// existed.</para>
    /// </summary>
    private const string ClaimSql =
        """
        SELECT id
        FROM events
        WHERE tenant_id = @tenantId
          AND status = 'PendingConfirmation'
          AND confirmation_deadline <= @now
        ORDER BY confirmation_deadline
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    public async Task<int> ConfirmExpiredAsync(
        TenantId tenantId, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        var claimed = await ClaimAsync(tenantId, now, batchSize, connection, pgTransaction, cancellationToken);
        if (claimed.Count == 0)
        {
            // Nothing to do. Committing an empty transaction rather than rolling back for the sake of
            // it - both release the (zero) locks, and one exit path is easier to reason about than
            // two.
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        // Loaded through the same context, so these are the rows the claim just locked. No xmin
        // conflict is possible here and that is not luck: nothing else can take the lock until this
        // transaction ends.
        var events = await db.Events
            .Where(e => claimed.Contains(e.Id))
            .ToListAsync(cancellationToken);

        foreach (var booking in events)
        {
            // The state machine still runs. The claim's predicate already guarantees the status, so
            // this cannot throw - which is the point of keeping it: the day somebody widens the
            // predicate, the aggregate refuses rather than silently confirming something that was
            // never pending.
            booking.Confirm(now);

            var confirmed = booking.DomainEvents.OfType<EventConfirmed>().Single();
            outbox.Enqueue(BookingConfirmedMapper.ToEnvelope(confirmed, idGenerator));
            booking.ClearDomainEvents();
        }

        // One SaveChangesAsync for the transitions and the outbox rows together - which is what makes
        // "the state change and its integration event are committed in one transaction" true
        // (CLAUDE.md rule 4). IOutboxWriter stages onto this same context and performs no I/O of its
        // own (adr/0017), so there is no second thing to keep in step.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return events.Count;
    }

    /// <summary>
    /// The same predicate the claim uses, minus the lock and the batch bound - a measurement, not a
    /// decision, so nothing branches on it and it needs no transaction of its own.
    ///
    /// <para>LINQ rather than the claim's SQL with <c>FOR UPDATE</c> removed by hand: two hand-kept
    /// copies of one predicate is how a count comes to report a healthy zero while rows sit
    /// unconfirmed, which is exactly the failure this number exists to notice. It reads through the
    /// same <c>ix_events_pending_confirmation</c> partial index either way.</para>
    /// </summary>
    public Task<int> CountOverdueAsync(
        TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Events.CountAsync(
            e => e.TenantId == tenantId
                && e.Status == EventStatus.PendingConfirmation
                && e.ConfirmationDeadline <= now,
            cancellationToken);

    private static async Task<List<EventId>> ClaimAsync(
        TenantId tenantId,
        DateTimeOffset now,
        int batchSize,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClaimSql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<EventId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new EventId(reader.GetGuid(0)));
        }

        return claimed;
    }
}
