using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `26-208`/`adr/0187`: the operator reschedule, in one transaction - a raw atomic claim of the new
/// run straight into <c>Booked</c>, an EF load-mutate-save cancelling the old run, and the one
/// <c>BookingRescheduled</c> outbox row, all committed together or not at all.
///
/// <para>The shape is <see cref="ExpiredBookingConfirmer"/>'s, not a new one: a raw Npgsql statement
/// and an EF transition on the <see cref="AgoCalendarDbContext"/>'s own connection, inside one
/// transaction opened below. The claim is raw for the same reason <see cref="BookingStore"/>'s is - its
/// verdict <i>is</i> its rows-affected count, a compare-and-set EF Core cannot express as one round
/// trip (CLAUDE.md rule 8). The cancel is EF because it is an ordinary single-actor aggregate write
/// protected by <c>xmin</c>, exactly like <c>CancelBookingHandler</c>'s. That both must land in one
/// transaction is the whole reason this is its own port (see <see cref="IBookingRescheduleStore"/>).</para>
///
/// <para><b>Claim first, then cancel.</b> The contended write goes first: if the new run cannot be
/// claimed in full, the transaction rolls back before the old run is touched at all, so a lost race
/// leaves the old booking <see cref="EventStatus.Booked"/>, untouched (`adr/0187` §Decision step 3).
/// If the claim succeeds but the old run is no longer cancellable (another writer changed it in the
/// gap), <see cref="Event.Cancel"/> throws and the transaction rolls back the claim too - the move is
/// atomic in both directions.</para>
/// </summary>
public sealed class BookingRescheduleStore(
    AgoCalendarDbContext db, IOutboxWriter outbox) : IBookingRescheduleStore
{
    /// <summary>
    /// The claim, generalising <see cref="BookingStore"/>'s own <c>ClaimSlotSql</c> to a reschedule:
    /// the new run lands straight in <c>'Booked'</c> with <c>confirmation_deadline = NULL</c> (an
    /// operator placing an appointment is the confirmation authority - it skips the veto window,
    /// `adr/0187`), rather than <c>'PendingConfirmation'</c> with a deadline.
    ///
    /// <para>Everything a legal claim must be true about is in the <c>WHERE</c> clause, evaluated under
    /// each row's own lock in the statement that changes it: <c>status = 'Available'</c> (the
    /// compare-and-set - two writers racing one shared row, only one wins), <c>id = ANY(@eventIds)</c>
    /// (the whole run, one statement, ADR-0086's amendment of the single-row form),
    /// <c>calendar_id = @calendarId</c> (a slot on another calendar is unclaimable by construction),
    /// and <c>starts_at &gt; @now</c> (<see cref="Event.Claim"/>'s own precondition, restated where it
    /// cannot go stale). Rows-affected must equal the run's length; a partial match means at least one
    /// slot was taken, and the caller rolls the whole attempt back.</para>
    /// </summary>
    private const string ClaimNewRunSql =
        """
        UPDATE events
        SET status = 'Booked',
            person_id = @personId,
            service_id = @serviceId,
            confirmation_deadline = NULL,
            booking_id = @bookingId,
            origin_conversation_id = @originConversationId
        WHERE id = ANY(@eventIds)
          AND calendar_id = @calendarId
          AND status = 'Available'
          AND starts_at > @now
        RETURNING id
        """;

    public async Task<bool> TryRescheduleAsync(
        BookingRescheduleRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        var claimedCount = await ClaimNewRunAsync(request, connection, pgTransaction, cancellationToken);
        if (claimedCount != request.NewEventIds.Count)
        {
            // At least one slot of the new run was taken, blocked, started, or on another calendar
            // between the handler's courtesy read and this statement. Roll the whole attempt back -
            // the old booking is not touched yet, so it stays Booked. An ordinary outcome, not a fault
            // (IBookingRescheduleStore's own contract; the same posture BookingStore takes).
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // The old run, cancelled through the aggregate - the identical load-mutate-save
        // CancelBookingHandler performs, sharing this transaction with the raw claim above so the two
        // commit together. Loaded through the same context, so these are the rows the handler already
        // read (identity map); Event.Cancel enforces the Booked/Pending precondition and its own xmin
        // is checked on SaveChanges below.
        var oldRun = await db.Events
            .Where(e => e.BookingId == request.PreviousBookingId)
            .ToListAsync(cancellationToken);

        try
        {
            foreach (var slot in oldRun)
            {
                // Booked -> Cancelled, CancelledByOperator. EventCancelled is raised and then cleared,
                // not staged: `adr/0187` stages one clean BookingRescheduled for the whole move, never
                // a Cancelled signal for the old run - the same "the cancel signal is not staged" shape
                // CancelBookingHandler/RejectBookingHandler already follow.
                slot.Cancel(request.Now);
                slot.ClearDomainEvents();
            }
        }
        catch (InvalidEventStateException)
        {
            // The old run stopped being cancellable in the gap (already cancelled by another writer,
            // say). Discard the in-memory mutations and let the transaction roll the claim back too -
            // the whole move is atomic. Rethrown for the handler to map to its ordinary invalid-state
            // failure.
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        // The one BookingRescheduled row, staged onto the outbox on the success path only, in this same
        // transaction (rule 4). IOutboxWriter.Enqueue performs no I/O - it stages onto this context's
        // change tracker, and the SaveChangesAsync below flushes it alongside the EF cancel, exactly as
        // BookingStore pairs its own Enqueue with one SaveChangesAsync under an explicit transaction.
        outbox.Enqueue(request.RescheduledEvent);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer changed a row of the old run between the load above and this save. A
            // failed SaveChangesAsync untracks nothing, so clear the tracker before the transaction
            // rolls back - the same care EventRepository takes - and surface the domain-level conflict
            // rather than an ORM type (the handler maps it to booking.concurrency_conflict).
            db.ChangeTracker.Clear();
            throw new EventConcurrencyConflictException(request.PreviousBookingId);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<int> ClaimNewRunAsync(
        BookingRescheduleRequest request,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // The new run's own anchor - NewEventIds[0], ConsecutiveRunFinder's own contract (the chosen
        // starting slot is always first). Written identically onto every claimed row's booking_id, so
        // the moved booking is a proper run resolvable from any member.
        var newAnchor = request.NewEventIds[0];

        await using var command = new NpgsqlCommand(ClaimNewRunSql, connection, transaction);
        command.Parameters.AddWithValue("eventIds", request.NewEventIds.Select(id => id.Value).ToArray());
        command.Parameters.AddWithValue("bookingId", newAnchor.Value);
        command.Parameters.AddWithValue("calendarId", request.CalendarId.Value);
        command.Parameters.AddWithValue("personId", request.PersonId);
        command.Parameters.AddWithValue("serviceId", request.ServiceId.Value);
        command.Parameters.AddWithValue("now", request.Now);
        command.Parameters.AddWithValue(
            "originConversationId", (object?)request.OriginConversationId ?? DBNull.Value);

        var rowsClaimed = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rowsClaimed++;
        }

        return rowsClaimed;
    }
}
