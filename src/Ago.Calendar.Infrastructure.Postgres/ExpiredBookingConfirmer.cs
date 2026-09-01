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
    /// <c>WaitingConversationClaimQuery</c>'s query shape, with this product's own predicate - and
    /// `20-18`'s own addition: this claims only <b>anchor</b> rows, one per booking
    /// (<c>id = booking_id</c> is true for exactly one row of a run, the run's own anchor - see
    /// <see cref="Event.BookingId"/>). A batch now bounds how many <i>bookings</i> one tick confirms,
    /// not how many event rows - the correct unit, since a booking's own rows must be confirmed
    /// together and a row-counted <c>LIMIT</c> could otherwise cut a run in half between two ticks or,
    /// worse, between two racing replicas.
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
    /// existed, and it still serves this one: <c>id = booking_id</c> is an extra filter evaluated
    /// against the rows the index already narrowed down, not a second index this item needed to add.</para>
    ///
    /// <para><c>SKIP LOCKED</c> is still here, and still for the reason it always was: two
    /// <c>Ago.Calendar.Worker</c> replicas sweeping the same tenant must split the backlog, not queue
    /// behind each other. Locking only the anchor here - one row per booking - is what makes
    /// <c>SKIP LOCKED</c> safe for a multi-row booking where it would not otherwise be: a plain
    /// <c>SKIP LOCKED</c> over every row of every due booking could let two replicas each lock a
    /// *different* row of the *same* run (row 1 already locked by replica A, row 2 not yet, so replica
    /// B's own scan locks row 2 for itself) - a split claim across two transactions that would confirm
    /// half a booking twice, from two different processes. Locking one representative row per booking
    /// first closes that window: of two replicas racing for the same booking, exactly one locks its
    /// anchor, and <see cref="ClaimGroupMembersSql"/> - the second statement, run only by whichever
    /// replica won this one - is the only place either replica ever reaches for that booking's other
    /// rows.</para>
    /// </summary>
    private const string ClaimAnchorsSql =
        """
        SELECT booking_id
        FROM events
        WHERE tenant_id = @tenantId
          AND status = 'PendingConfirmation'
          AND confirmation_deadline <= @now
          AND id = booking_id
        ORDER BY confirmation_deadline
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    /// <summary>
    /// Locks and returns every row of the bookings <see cref="ClaimAnchorsSql"/> just won. A plain
    /// <c>FOR UPDATE</c>, deliberately without <c>SKIP LOCKED</c>: by the time this runs, this
    /// transaction already holds the exclusive lock on each booking's own anchor, and no other
    /// <c>Ago.Calendar.Worker</c> replica can have reached this statement for the same booking without
    /// having first won that same anchor lock - which, since locks are exclusive, only one transaction
    /// ever can. The only thing this statement can still block on is an operator's own
    /// cancel/reject/no-show transaction touching one of these rows directly
    /// (<c>CancelBookingHandler</c> and siblings) - the pre-existing, deliberate "this handler races
    /// the sweep" contention <c>RejectBookingHandler</c>'s own remarks describe, now waited out rather
    /// than raced, which is an acceptable cost for a background tick and not for the customer-facing
    /// claim path <c>BookingStore</c> never blocks on.
    ///
    /// <para>The predicate is re-checked here rather than trusted from the first statement's own
    /// result, for the same reason a claim always re-checks under its own lock: holding the anchor's
    /// lock pins the *booking's* status only once every writer of that booking (the sweep included)
    /// updates every row of it inside one transaction, which is exactly what
    /// <c>CancelBookingHandler</c>/<c>RejectBookingHandler</c>/<c>MarkNoShowHandler</c> do after
    /// `20-18` - but re-checking costs nothing extra here (the same index, the same rows) and turns
    /// "we believe this is still true" into "the database just confirmed it is".</para>
    /// </summary>
    private const string ClaimGroupMembersSql =
        """
        SELECT id
        FROM events
        WHERE booking_id = ANY(@bookingIds)
          AND status = 'PendingConfirmation'
          AND confirmation_deadline <= @now
        FOR UPDATE
        """;

    public async Task<int> ConfirmExpiredAsync(
        TenantId tenantId, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        var bookingIds = await ClaimAnchorsAsync(
            tenantId, now, batchSize, connection, pgTransaction, cancellationToken);
        if (bookingIds.Count == 0)
        {
            // Nothing to do. Committing an empty transaction rather than rolling back for the sake of
            // it - both release the (zero) locks, and one exit path is easier to reason about than
            // two.
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var claimedEventIds = await ClaimGroupMembersAsync(
            bookingIds, now, connection, pgTransaction, cancellationToken);

        // Loaded through the same context, so these are the rows the two claims above just locked. No
        // xmin conflict is possible here and that is not luck: nothing else can take the lock on any
        // of them until this transaction ends.
        var events = await db.Events
            .Where(e => claimedEventIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var byBooking = events
            .GroupBy(e => e.BookingId!.Value)
            .Select(group => group.OrderBy(e => e.StartsAt).ToList());

        foreach (var group in byBooking)
        {
            // The run's own overall end - the last slot's, buffers between them included - so the one
            // BookingConfirmed message this group produces describes the whole booking a customer
            // made, not only its first slot. See BookingConfirmedMapper's own remarks on groupEndsAt.
            var groupEndsAt = group[^1].EndsAt;
            EventConfirmed? anchorConfirmed = null;

            foreach (var booking in group)
            {
                // The state machine still runs, for every row of the run. The claim's own predicate
                // already guarantees the status, so this cannot throw - which is the point of keeping
                // it: the day somebody widens the predicate, the aggregate refuses rather than
                // silently confirming something that was never pending.
                booking.Confirm(now);

                if (booking.Id == booking.BookingId)
                {
                    anchorConfirmed = booking.DomainEvents.OfType<EventConfirmed>().Single();
                }

                booking.ClearDomainEvents();
            }

            // Exactly one outbox row per booking, not per slot - a three-slot run confirming would
            // otherwise stage three BookingConfirmed messages for one appointment, which `20-05`'s SMS
            // consumer would turn into three identical texts to one customer.
            outbox.Enqueue(BookingConfirmedMapper.ToEnvelope(anchorConfirmed!, idGenerator, groupEndsAt));
        }

        // One SaveChangesAsync for every row's transition and every group's outbox row together -
        // which is what makes "the state change and its integration event are committed in one
        // transaction" true (CLAUDE.md rule 4). IOutboxWriter stages onto this same context and
        // performs no I/O of its own (adr/0017), so there is no second thing to keep in step.
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

    private static async Task<List<Guid>> ClaimAnchorsAsync(
        TenantId tenantId,
        DateTimeOffset now,
        int batchSize,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClaimAnchorsSql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var bookingIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bookingIds.Add(reader.GetGuid(0));
        }

        return bookingIds;
    }

    private static async Task<List<EventId>> ClaimGroupMembersAsync(
        List<Guid> bookingIds,
        DateTimeOffset now,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClaimGroupMembersSql, connection, transaction);
        command.Parameters.AddWithValue("bookingIds", bookingIds.ToArray());
        command.Parameters.AddWithValue("now", now);

        var claimed = new List<EventId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new EventId(reader.GetGuid(0)));
        }

        return claimed;
    }
}
