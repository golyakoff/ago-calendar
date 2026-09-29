using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `26-275`/`adr/0189`: the person-erasure write - see <see cref="IPersonEraseStore"/>'s own remarks
/// for the shape and the reasoning. Two raw statements and one staged outbox row's own
/// <c>SaveChangesAsync</c>, all in one transaction on <see cref="AgoCalendarDbContext"/>'s own
/// connection - the identical "raw statement for every compare-and-set, one EF flush for the outbox"
/// shape <see cref="ManualBookingStore"/> and <see cref="BookingRescheduleStore"/> both already
/// establish.
/// </summary>
public sealed class PersonEraseStore(AgoCalendarDbContext db, IOutboxWriter outbox) : IPersonEraseStore
{
    /// <summary>
    /// Nulls <c>person_id</c> on every event this person ever touched, <b>except</b> a live future
    /// booking - never a row this handler's own guard would refuse to erase around, regardless of
    /// whether that row existed when this statement's own transaction began or was claimed a moment
    /// before it ran. That is what makes this statement safe to run <em>before</em> the guarded delete
    /// below rather than needing a separate "did anything change since the guard read" recheck: a row
    /// this predicate would exclude is never touched no matter when Postgres evaluates it, so there is
    /// nothing for a race to corrupt.
    ///
    /// <para><b>Author decision on issue 1815 - anonymise, never delete.</b> Every aggregate-by-service/
    /// worker/date column on the remaining rows is untouched; only the one column that named this
    /// person is cleared, which is also what a real foreign key to <c>person_records</c>
    /// (<c>EventConfiguration</c>'s own remarks) requires before that row can be deleted at all. No
    /// "recompute analytics" trigger is published alongside this - `ago_analytics`'s own rollups over
    /// these rows stay exactly as they were.</para>
    /// </summary>
    private const string AnonymizePastEventsSql =
        """
        UPDATE events
        SET person_id = NULL
        WHERE tenant_id = @tenantId
          AND person_id = @personId
          AND NOT (status IN ('Booked', 'PendingConfirmation') AND starts_at > @now)
        """;

    /// <summary>
    /// The guard and the delete, one statement - the identical idiom
    /// <see cref="WorkerRepository.DeleteIfNeverBookedAsync"/> already established for the analogous
    /// "may this row be deleted, or does live booking state say no" question, restated here per
    /// CLAUDE.md rule 8: the compare-and-set read is the delete's own <c>WHERE NOT EXISTS</c>, evaluated
    /// by the database inside this transaction, never a courtesy read this handler trusted before it.
    /// Zero rows affected is the whole verdict, and it is deliberately ambiguous between "still has a
    /// future booking" and "already gone" - <see cref="TryEraseAsync"/>'s own caller
    /// (<c>ErasePersonHandler</c>) re-reads to tell them apart, after the fact, the same way
    /// <c>DeleteWorkerHandler</c> already does for its own guarded delete.
    /// </summary>
    private const string GuardedDeletePersonRecordSql =
        """
        DELETE FROM person_records p
        WHERE p.person_id = @personId
          AND p.tenant_id = @tenantId
          AND NOT EXISTS (
              SELECT 1 FROM events e
              WHERE e.tenant_id = @tenantId
                AND e.person_id = @personId
                AND e.status IN ('Booked', 'PendingConfirmation')
                AND e.starts_at > @now
          )
        """;

    public async Task<bool> TryEraseAsync(PersonEraseAttempt attempt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        await AnonymizePastEventsAsync(attempt, connection, pgTransaction, cancellationToken);

        var erased = await GuardedDeletePersonRecordAsync(attempt, connection, pgTransaction, cancellationToken);
        if (!erased)
        {
            // Blocked by a live future booking, or the row was already gone - either way, nothing here
            // commits: the anonymising update above rolls back with everything else, so a refused
            // attempt leaves the person's events exactly as they were. The identical
            // "roll back, write nothing, stage nothing" posture every claim-shaped store in this
            // product already takes for its own lost race (ManualBookingStore.TryEnterAsync's own
            // remarks).
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // Staged only on the success path, inside the same transaction as the delete and the
        // anonymising update (CLAUDE.md rule 4) - a blocked or already-gone attempt must never tell
        // chat a person was erased that was not.
        outbox.Enqueue(attempt.PersonErasedEvent);

        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task AnonymizePastEventsAsync(
        PersonEraseAttempt attempt,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AnonymizePastEventsSql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", attempt.TenantId.Value);
        command.Parameters.AddWithValue("personId", attempt.PersonId);
        command.Parameters.AddWithValue("now", attempt.Now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> GuardedDeletePersonRecordAsync(
        PersonEraseAttempt attempt,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GuardedDeletePersonRecordSql, connection, transaction);
        command.Parameters.AddWithValue("personId", attempt.PersonId);
        command.Parameters.AddWithValue("tenantId", attempt.TenantId.Value);
        command.Parameters.AddWithValue("now", attempt.Now);

        var rowsDeleted = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsDeleted == 1;
    }
}
