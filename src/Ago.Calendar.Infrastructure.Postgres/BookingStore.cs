using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// Two statements, one transaction, no reads that a decision depends on.
///
/// <para><b>Raw SQL, and the exact reason it is raw.</b> Both statements below are compare-and-set
/// shaped, and EF Core cannot express either as one round trip. The claim's verdict *is* its
/// rows-affected count; the upsert's <c>ON CONFLICT</c> arbitration happens inside Postgres against
/// a unique index. Written through EF's LINQ they would each become a read followed by a write, with
/// a window in between that a concurrent caller walks through - and EF's own answer to that window,
/// optimistic concurrency, converts an ordinary lost race into an exception on the hottest path in
/// the product. adr/0004's "EF for writes" default holds everywhere else in this adapter; this is a
/// stated exception with a qualifying reason, the same exception `4-01` took for
/// <c>operators.active_chats</c>, and `20-02` took for <c>ON CONFLICT DO NOTHING</c>.</para>
///
/// <para>Issued through <see cref="AgoCalendarDbContext"/>'s own connection rather than a separate
/// <see cref="NpgsqlDataSource"/>, so both statements sit inside the transaction opened below and a
/// future caller with an ambient transaction gets the same behaviour for free - the shape ago-chat's
/// <c>OperatorCapacityStore</c> settled on after `4-02` needed exactly that.</para>
/// </summary>
public sealed class BookingStore(AgoCalendarDbContext db) : IBookingStore
{
    /// <summary>
    /// The lead card, found-or-created in one statement.
    ///
    /// <para><c>ON CONFLICT ... DO UPDATE</c>, not <c>DO NOTHING</c>: <c>DO UPDATE</c> is what makes
    /// the statement return a row in both cases, so the caller learns the customer's id whether it
    /// inserted or collided. With <c>DO NOTHING</c> a collision returns nothing and the caller has to
    /// go and read the row it just failed to insert - a second round trip, and a branch that only
    /// executes under contention, which is the branch least likely to be exercised before
    /// production.</para>
    ///
    /// <para><c>last_seen_at</c> takes the greater of the two values rather than the incoming one, so
    /// a request that was slow in flight cannot rewind the watermark - the same rule
    /// <see cref="Customer.Touch"/> enforces in memory, restated here because this statement never
    /// goes through that method.</para>
    ///
    /// <para><c>display_name</c> keeps whatever is already there and only fills a blank. An operator
    /// who corrected "jon" to "Jonathan Reed" on the lead card has curated that field; the next
    /// booking from that phone must not silently undo it because the customer typed the short form
    /// into a public form again.</para>
    ///
    /// <para><b>`20-09`: <c>phone_verified_at</c> follows the identical "keep what's already there"
    /// rule as <c>display_name</c>, for a related but distinct reason.</b> <see cref="BookingAttempt"/>'s
    /// own value is always non-null (the caller already refused to reach this point without one), but
    /// once a phone has been proven reachable, that proof does not expire just because a later booking
    /// from the same number happens to arrive with an older or (structurally impossible today, but not
    /// worth relying on) different assertion - the first verification is the honest "since when" answer
    /// (`RouteConversationToModuleHandler`'s own remarks on Chat's side make the identical choice,
    /// reading <c>ChannelIdentity.FirstSeenAt</c> rather than "now"). <c>COALESCE</c> in this direction
    /// means a customer row can only ever move from unverified to verified, never the reverse and never
    /// to a different timestamp once set.</para>
    /// </summary>
    private const string UpsertCustomerSql =
        """
        INSERT INTO customers (id, tenant_id, phone, display_name, phone_verified_at, no_show_count, first_seen_at, last_seen_at)
        VALUES (@id, @tenantId, @phone, @displayName, @phoneVerifiedAt, 0, @now, @now)
        ON CONFLICT (tenant_id, phone) DO UPDATE
            SET last_seen_at = GREATEST(customers.last_seen_at, EXCLUDED.last_seen_at),
                display_name = COALESCE(customers.display_name, EXCLUDED.display_name),
                phone_verified_at = COALESCE(customers.phone_verified_at, EXCLUDED.phone_verified_at)
        RETURNING id
        """;

    /// <summary>
    /// The claim. Everything a booking must be true about is in the <c>WHERE</c> clause, evaluated
    /// under the row's own lock in the same statement that changes it.
    ///
    /// <list type="bullet">
    ///   <item><c>status = 'Available'</c> - the compare-and-set. Of two simultaneous callers,
    ///   Postgres gives one 1 row and the other 0. Neither needs to know the other existed.</item>
    ///   <item><c>calendar_id = @calendarId</c> - an event id belonging to another calendar is
    ///   unclaimable by construction, not by a validation a future caller could forget. The endpoint
    ///   is unauthenticated, so the route's own calendar id is the only thing tying the request to a
    ///   tenant, and it has to bind to the write itself.</item>
    ///   <item><c>starts_at &gt; @now</c> - <see cref="Event.Claim"/>'s own precondition, restated
    ///   where it cannot go stale. Checked in application code it would be checked against a reading
    ///   of the row from milliseconds ago.</item>
    /// </list>
    ///
    /// <para><b>`20-09`: deliberately does <em>not</em> add a <c>phone_verified_at IS NOT NULL</c>
    /// condition here, and that is a considered choice, not an oversight.</b> The reason every other
    /// condition above lives in this <c>WHERE</c> clause is that each checks a fact read from a row
    /// other callers are actively contending for at the instant of the check - staleness is the risk a
    /// separate application-code read-then-decide cannot close. <see cref="BookingAttempt.PhoneVerifiedAt"/>
    /// is not such a fact: it is a value the caller supplies directly on the very attempt this statement
    /// is already processing, with no interceding read of anything that could change between a
    /// hypothetical check and this write - <c>BookEventHandler</c>'s own remarks make the identical
    /// argument for why refusing an unverified attempt there, before this method is ever called, is
    /// safe. Making the type non-nullable (this parameter cannot be <see langword="null"/>) is the
    /// stronger guarantee besides: it holds for every future caller of this port by construction, not
    /// only for the one caller that happens to remember a runtime check.</para>
    ///
    /// <para><c>RETURNING</c> hands back what the confirmation needs, from the write itself. A
    /// follow-up <c>SELECT</c> would be a second round trip reading a row that `20-04`'s sweep could
    /// already have moved on - the values below are the ones this statement wrote.</para>
    ///
    /// <para>The statement never touches <c>no_show_count</c> or any other lead-card field: a
    /// booking is a fact about a slot, and conflating the two writes is how one contended statement
    /// grows a second reason to contend.</para>
    /// </summary>
    private const string ClaimSlotSql =
        """
        UPDATE events
        SET status = 'PendingConfirmation',
            customer_id = @customerId,
            service_id = @serviceId,
            confirmation_deadline = @deadline
        WHERE id = @eventId
          AND calendar_id = @calendarId
          AND status = 'Available'
          AND starts_at > @now
        RETURNING worker_id, starts_at, ends_at, local_date
        """;

    public async Task<BookingConfirmation?> TryBookAsync(
        BookingAttempt attempt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        var customerId = await UpsertCustomerAsync(attempt, connection, pgTransaction, cancellationToken);

        var confirmation = await ClaimAsync(attempt, customerId, connection, pgTransaction, cancellationToken);
        if (confirmation is null)
        {
            // The slot went to somebody else between this request arriving and this statement
            // running - or was never claimable. Rolling back is what keeps the promise
            // IBookingStore makes about personal data: a booking that did not happen leaves no lead
            // card behind. Explicit rather than relying on the `await using` above, so the intent is
            // legible at the point the decision is made.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return confirmation;
    }

    private static async Task<CustomerId> UpsertCustomerAsync(
        BookingAttempt attempt,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpsertCustomerSql, connection, transaction);
        command.Parameters.AddWithValue("id", attempt.NewCustomerId.Value);
        command.Parameters.AddWithValue("tenantId", attempt.TenantId.Value);
        command.Parameters.AddWithValue("phone", attempt.Phone.Value);
        command.Parameters.AddWithValue("displayName", Blank(attempt.DisplayName) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("phoneVerifiedAt", (object?)attempt.PhoneVerifiedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("now", attempt.Now);

        // Always non-null: DO UPDATE returns a row on both the insert and the conflict path, which is
        // the whole reason it is DO UPDATE.
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return new CustomerId((Guid)id!);
    }

    private static async Task<BookingConfirmation?> ClaimAsync(
        BookingAttempt attempt,
        CustomerId customerId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClaimSlotSql, connection, transaction);
        command.Parameters.AddWithValue("eventId", attempt.EventId.Value);
        command.Parameters.AddWithValue("calendarId", attempt.CalendarId.Value);
        command.Parameters.AddWithValue("customerId", customerId.Value);
        command.Parameters.AddWithValue("serviceId", attempt.ServiceId.Value);
        command.Parameters.AddWithValue("deadline", attempt.ConfirmationDeadline);
        command.Parameters.AddWithValue("now", attempt.Now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No row is the verdict. Not an exception, not a special value to interpret - the absence of
        // an updated row *is* "somebody else has it".
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BookingConfirmation(
            attempt.EventId,
            customerId,
            new WorkerId(reader.GetGuid(0)),
            new TimeSlot(reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2)),
            reader.GetFieldValue<DateOnly>(3));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
