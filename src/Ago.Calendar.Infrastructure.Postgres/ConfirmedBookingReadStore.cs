using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `23-34`'s own instance of the shape <c>PendingBookingReadStore</c> (`20-12`)
/// established: Dapper over a plain <see cref="NpgsqlDataSource"/>, its own connection rather than
/// <c>AgoCalendarDbContext</c>'s - restated rather than shared, the identical reasoning every read
/// store in this product already gives for itself.
///
/// <para><b>One query, not two.</b> Unlike <c>PendingBookingReadStore</c>/<c>WorkerSlotReadStore</c>,
/// there is no "without contact data" variant here - the handler never calls this store at all for a
/// caller lacking <see cref="Permission.CustomerRead"/> (<see cref="IConfirmedBookingReadStore"/>'s
/// own remarks on why), so every row this method ever produces already has a customer to join to.</para>
/// </summary>
public sealed class ConfirmedBookingReadStore(NpgsqlDataSource dataSource) : IConfirmedBookingReadStore
{
    /// <summary>
    /// <c>workers</c> is an inner join - every <see cref="EventStatus.Booked"/> row's own worker
    /// always still has a row (<see cref="ConfirmedBookingRow.WorkerDisplayName"/>'s own remarks on
    /// why a booked worker can never be deleted, only deactivated). <c>services</c> stays a
    /// <c>left join</c>, the same defensive caution <c>WorkerSlotReadStore</c> takes for a foreign key
    /// this product's own rules do not otherwise let go stale - cheaper to leave nullable than to
    /// assert a second invariant nothing enforces today. <c>customers</c> is joined unconditionally
    /// too: <see cref="IConfirmedBookingReadStore"/>'s own remarks explain why this store has no
    /// contact-free caller to spare a join for.
    ///
    /// <para><c>group by booking_id</c> and its own functionally-dependent columns, the identical
    /// <c>20-18</c> shape <c>PendingBookingReadStore</c> already established: every column besides
    /// <c>starts_at</c>/<c>ends_at</c> is identical across every row of one booking by construction
    /// (the claim writes them once, onto every row of the run, in the same statement), so grouping by
    /// all of them together with <c>booking_id</c> is exact, not an aggregation choice.</para>
    /// </summary>
    private const string Sql =
        """
        select e.booking_id as "EventId", e.calendar_id as "CalendarId", e.worker_id as "WorkerId",
               w.display_name as "WorkerDisplayName", e.service_id as "ServiceId", s.name as "ServiceName",
               e.customer_id as "CustomerId", c.display_name as "CustomerDisplayName",
               min(e.starts_at) as "StartsAt", max(e.ends_at) as "EndsAt", e.local_date as "LocalDate",
               c.phone as "Phone"
        from events e
        join workers w on w.id = e.worker_id
        left join services s on s.id = e.service_id
        left join customers c on c.id = e.customer_id
        where e.tenant_id = @TenantId
          and e.status = 'Booked'
          and e.local_date >= @From::date
          and e.local_date <= @To::date
        group by e.booking_id, e.calendar_id, e.worker_id, w.display_name, e.service_id, s.name,
                 e.customer_id, c.display_name, e.local_date, c.phone
        order by e.local_date, w.display_name, min(e.starts_at)
        """;

    public async Task<IReadOnlyList<ConfirmedBookingRow>> GetConfirmedForTenantAsync(
        TenantId tenantId, DateOnly from, DateOnly to, bool mask, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ConfirmedBookingQueryRow>(new CommandDefinition(
            Sql,
            // `DateOnly` cannot be bound as a Dapper input parameter directly - `WorkerSlotReadStore`'s
            // own remarks explain why a `DateTime`-at-midnight plus an explicit `::date` cast in SQL
            // is the fix kept local to this file rather than a process-wide type handler.
            new
            {
                TenantId = tenantId.Value,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => ToRow(row, mask))];
    }

    private static ConfirmedBookingRow ToRow(ConfirmedBookingQueryRow row, bool mask) => new(
        new EventId(row.EventId),
        new CalendarId(row.CalendarId),
        new WorkerId(row.WorkerId),
        row.WorkerDisplayName,
        // Non-null on any Booked row - Event.Claim sets it together with the customer, and no
        // transition ever clears it. Asserted with `!`, the identical shape
        // PendingBookingReadStore.ToRow already uses for the same two columns: a null here would mean
        // the state machine had been bypassed, and inventing an empty id would hide that instead of
        // surfacing it.
        new ServiceId(row.ServiceId!.Value),
        row.ServiceName,
        new CustomerId(row.CustomerId!.Value),
        row.CustomerDisplayName,
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        row.LocalDate,
        (int)row.LocalDate.DayOfWeek,
        // The real value is never assigned to Phone when mask is true - `23-12`'s own rule,
        // restated the way `ContactsReadStore.ToRow`'s own remarks give it: there is no flag
        // downstream of this method a careless edit could ignore and forward the unmasked number.
        mask ? new PhoneNumber(row.Phone!).Masked() : row.Phone!,
        mask);

    /// <summary>The raw shape Dapper materialises, separate from <see cref="ConfirmedBookingRow"/> so
    /// the Application-facing row can hold strongly-typed ids and <see cref="DateTimeOffset"/>s while
    /// this one holds what Npgsql hands back.</summary>
    private sealed record ConfirmedBookingQueryRow(
        Guid EventId,
        Guid CalendarId,
        Guid WorkerId,
        string WorkerDisplayName,
        Guid? ServiceId,
        string? ServiceName,
        Guid? CustomerId,
        string? CustomerDisplayName,
        DateTime StartsAt,
        DateTime EndsAt,
        DateOnly LocalDate,
        string? Phone);
}
