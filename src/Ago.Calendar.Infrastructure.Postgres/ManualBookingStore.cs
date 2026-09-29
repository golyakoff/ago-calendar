using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `26-268`/`adr/0188`: the manual-entry write - a raw person-record insert, a raw atomic claim
/// straight into <c>Booked</c>, and the two staged outbox rows' own <c>SaveChangesAsync</c>, all in one
/// transaction on <see cref="AgoCalendarDbContext"/>'s own connection. The identical shape
/// <see cref="BookingStore"/> and <see cref="BookingRescheduleStore"/> both already establish: a raw
/// Npgsql statement for every compare-and-set, one EF <c>SaveChangesAsync</c> to flush what the outbox
/// writer staged onto this context's own change tracker.
///
/// <para><b>Person first, claim second</b> - <see cref="BookingStore"/>'s own ordering, restated for
/// the same reason: the person row is locked before the contended claim runs, so two writers can never
/// deadlock against each other by locking the two rows in opposite orders. Before `26-268`§2a the person
/// id was always freshly minted (`adr/0188`/§3.4), so the <c>ON CONFLICT</c> arm below was unreachable in
/// production traffic, kept only for the identical defensive symmetry <see cref="BookingStore"/>'s own
/// upsert has. §2a's reuse path makes it reachable for real: reusing a recognised client re-runs this
/// same statement against an id that already has a row, which is exactly the "re-confirm phone, bump
/// last-seen" update the <c>ON CONFLICT</c> clause below already expresses - no SQL change was needed to
/// support it.</para>
/// </summary>
public sealed class ManualBookingStore(AgoCalendarDbContext db, IOutboxWriter outbox) : IManualBookingStore
{
    /// <summary>
    /// The person's thin operational record - inserted fresh on a mint, or re-confirmed on a
    /// `26-268`§2a reuse (the <c>ON CONFLICT</c> arm: phone, last-seen and the operator-confirmed
    /// timestamp all move forward; <c>phone_verified_at</c> and <c>no_show_count</c> are deliberately
    /// absent from the <c>SET</c> list so a reuse never clobbers an SMS proof or a no-show history the
    /// existing row already holds). On a fresh mint, <c>phone_verified_at</c> stays
    /// <see langword="null"/> - this person has proven nothing by SMS - and
    /// <c>operator_confirmed_phone_at</c> is set to <paramref name="attempt"/>'s own <c>Now</c>: the
    /// operator's "I called and it is them" fact (`23-12`/§3.3), the same column
    /// <see cref="PersonRecord.RecordOperatorConfirmedPhone"/> states in C#, here written directly by
    /// SQL for the identical reason <see cref="BookingStore"/>'s own upsert is raw rather than routed
    /// through the aggregate - the real, load-bearing write is the statement, not the domain method.
    /// </summary>
    private const string InsertPersonRecordSql =
        """
        INSERT INTO person_records
            (person_id, tenant_id, phone, phone_verified_at, operator_confirmed_phone_at, no_show_count, first_seen_at, last_seen_at)
        VALUES (@personId, @tenantId, @phone, NULL, @now, 0, @now, @now)
        ON CONFLICT (person_id) DO UPDATE
            SET last_seen_at = GREATEST(person_records.last_seen_at, EXCLUDED.last_seen_at),
                phone = EXCLUDED.phone,
                operator_confirmed_phone_at = EXCLUDED.operator_confirmed_phone_at
        RETURNING person_id
        """;

    /// <summary>
    /// The claim - <see cref="BookingRescheduleStore"/>'s own <c>ClaimNewRunSql</c>, generalising its
    /// "straight into <c>Booked</c>, no deadline" shape to a fresh person rather than a carried-forward
    /// one, and to <c>origin_conversation_id = NULL</c> unconditionally: a manual entry never has a chat
    /// origin (`adr/0188`/§4 - it is a direct command against the calendar, never a conversation).
    /// Every condition in the <c>WHERE</c> clause is evaluated under each row's own lock in the same
    /// statement that changes it, the identical "evaluating it and acting on it are one operation"
    /// property <see cref="BookingStore"/>'s own claim states for each of its own conditions.
    /// </summary>
    private const string ClaimRunSql =
        """
        UPDATE events
        SET status = 'Booked',
            person_id = @personId,
            service_id = @serviceId,
            confirmation_deadline = NULL,
            booking_id = @bookingId,
            origin_conversation_id = NULL
        WHERE id = ANY(@eventIds)
          AND calendar_id = @calendarId
          AND status = 'Available'
          AND starts_at > @now
        RETURNING id, worker_id, starts_at, ends_at, local_date
        """;

    public async Task<BookingConfirmation?> TryEnterAsync(
        ManualBookingAttempt attempt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        await InsertPersonRecordAsync(attempt, connection, pgTransaction, cancellationToken);

        var confirmation = await ClaimAsync(attempt, connection, pgTransaction, cancellationToken);
        if (confirmation is null)
        {
            // The run was not claimable in full - somebody else took it, part of it started, or it was
            // blocked between the handler's courtesy read and this statement. Rolling back keeps the
            // identical personal-data promise IBookingStore makes: a person minted for an entry that did
            // not happen leaves no record and no PersonRegistered announcement behind.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // Both envelopes were built by the handler (Application) from facts it already resolved; this
        // adapter only stages them, on the success path, inside the same transaction as the claim and
        // the person insert (rule 4) - the identical "stage inside the transaction, flush with one
        // SaveChangesAsync on the ambient transaction" shape BookingStore and BookingRescheduleStore both
        // already use.
        //
        // `26-268`§2a/`adr/0188`: PersonRegisteredEvent is null on the reuse path - the person already
        // has a chat-side registration from whichever earlier booking created this PersonId's own
        // record, and staging it again would tell chat to register an id it already knows. BookingConfirmed
        // stages unconditionally: a real booking was made either way.
        if (attempt.PersonRegisteredEvent is { } personRegistered)
        {
            outbox.Enqueue(personRegistered);
        }

        outbox.Enqueue(attempt.BookingConfirmedEvent);

        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return confirmation;
    }

    private static async Task InsertPersonRecordAsync(
        ManualBookingAttempt attempt,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertPersonRecordSql, connection, transaction);
        command.Parameters.AddWithValue("personId", attempt.PersonId);
        command.Parameters.AddWithValue("tenantId", attempt.TenantId.Value);
        command.Parameters.AddWithValue("phone", attempt.Phone.Value);
        command.Parameters.AddWithValue("now", attempt.Now);

        // Always one row back, on both the insert and the (unreachable, in practice) conflict path -
        // read and discarded, the identical "the person id is the caller's own input" shape
        // BookingStore's own upsert already establishes.
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<BookingConfirmation?> ClaimAsync(
        ManualBookingAttempt attempt,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // The run's own anchor - EventIds[0] by ManualBookingAttempt's own contract, the identical
        // convention BookingAttempt and BookingRescheduleRequest both already carry.
        var bookingId = attempt.EventIds[0];

        await using var command = new NpgsqlCommand(ClaimRunSql, connection, transaction);
        command.Parameters.AddWithValue("eventIds", attempt.EventIds.Select(id => id.Value).ToArray());
        command.Parameters.AddWithValue("bookingId", bookingId.Value);
        command.Parameters.AddWithValue("calendarId", attempt.CalendarId.Value);
        command.Parameters.AddWithValue("personId", attempt.PersonId);
        command.Parameters.AddWithValue("serviceId", attempt.ServiceId.Value);
        command.Parameters.AddWithValue("now", attempt.Now);

        var rowsClaimed = 0;
        WorkerId? workerId = null;
        DateTimeOffset? earliestStart = null;
        DateTimeOffset? latestEnd = null;
        DateOnly? localDate = null;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rowsClaimed++;
                workerId ??= new WorkerId(reader.GetGuid(1));
                var startsAt = reader.GetFieldValue<DateTimeOffset>(2);
                var endsAt = reader.GetFieldValue<DateTimeOffset>(3);
                earliestStart = earliestStart is null || startsAt < earliestStart ? startsAt : earliestStart;
                latestEnd = latestEnd is null || endsAt > latestEnd ? endsAt : latestEnd;
                localDate ??= reader.GetFieldValue<DateOnly>(4);
            }
        }

        // Fewer rows than the run named is the whole verdict - a partial match means at least one slot
        // of the run was unavailable, and the caller rolls the whole attempt back rather than accept a
        // torn claim, the identical rule BookingStore.ClaimAsync and BookingRescheduleStore.ClaimNewRunAsync
        // both already state.
        if (rowsClaimed != attempt.EventIds.Count)
        {
            return null;
        }

        return new BookingConfirmation(
            bookingId,
            attempt.EventIds,
            attempt.PersonId,
            workerId!.Value,
            new TimeSlot(earliestStart!.Value, latestEnd!.Value),
            localDate!.Value);
    }
}
