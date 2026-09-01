using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `20-15`'s own instance of the shape <c>PendingBookingReadStore</c> (`20-12`)
/// established: Dapper over a plain <see cref="NpgsqlDataSource"/>, its own connection rather than
/// <c>AgoCalendarDbContext</c>'s - the identical reasoning restated because this is a third,
/// independent read store rather than a shared base class nobody asked for.
/// </summary>
public sealed class WorkerSlotReadStore(NpgsqlDataSource dataSource) : IWorkerSlotReadStore
{
    /// <summary>
    /// <c>services</c> is joined unconditionally - a service name is the shop's own catalogue, never
    /// gated - while <c>customers</c> is not joined at all here. A literal <c>null::text</c> for both
    /// contact columns keeps this query's row shape identical to <see cref="SqlWithContactData"/>'s,
    /// which Dapper's constructor-matching materialisation requires: a C# record's default parameter
    /// value is a caller-side convenience only, not a second, shorter constructor
    /// (<c>PendingBookingReadStore</c>'s own remarks name the exact exception this produced the first
    /// time it was tried without the literal).
    /// </summary>
    private const string SqlWithoutContactData =
        """
        select e.id as "EventId", e.local_date as "LocalDate", e.starts_at as "StartsAt",
               e.ends_at as "EndsAt", e.status as "Status",
               e.service_id as "ServiceId", s.name as "ServiceName",
               e.customer_id as "CustomerId", null::text as "CustomerDisplayName", null::text as "Phone",
               e.booking_id as "BookingId"
        from events e
        left join services s on s.id = e.service_id
        where e.tenant_id = @TenantId
          and e.worker_id = @WorkerId
          and e.local_date >= @From::date
          and e.local_date <= @To::date
        order by e.starts_at
        """;

    /// <summary>
    /// `20-12`'s own shape: the only difference from <see cref="SqlWithoutContactData"/> is the join to
    /// <c>customers</c> and its two extra columns - chosen over always joining and hiding the result,
    /// so a caller without <c>customer:read</c> costs the database nothing extra. <c>left join</c>
    /// rather than an inner join, because plenty of rows here (every <c>Available</c> or
    /// <c>Blocked</c> slot) genuinely have no customer at all - unlike the pending queue, where every
    /// row does.
    /// </summary>
    private const string SqlWithContactData =
        """
        select e.id as "EventId", e.local_date as "LocalDate", e.starts_at as "StartsAt",
               e.ends_at as "EndsAt", e.status as "Status",
               e.service_id as "ServiceId", s.name as "ServiceName",
               e.customer_id as "CustomerId", c.display_name as "CustomerDisplayName", c.phone as "Phone",
               e.booking_id as "BookingId"
        from events e
        left join services s on s.id = e.service_id
        left join customers c on c.id = e.customer_id
        where e.tenant_id = @TenantId
          and e.worker_id = @WorkerId
          and e.local_date >= @From::date
          and e.local_date <= @To::date
        order by e.starts_at
        """;

    public async Task<IReadOnlyList<WorkerSlotRow>> GetForWorkerAsync(
        TenantId tenantId, WorkerId workerId, DateOnly from, DateOnly to, bool includeContactData,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkerSlotQueryRow>(new CommandDefinition(
            includeContactData ? SqlWithContactData : SqlWithoutContactData,
            // `DateOnly` cannot be bound as a Dapper input parameter without a registered
            // `SqlMapper.ITypeHandler` - unlike the *output* side, where every read store's own
            // `LocalDate` column already materialises into a `DateOnly` through Npgsql's native
            // support with no handler at all. A handler here would have to be registered process-
            // wide (`SqlMapper.AddTypeHandler` has no narrower scope) and would then intercept every
            // other read store's `DateOnly` output too, which is a bigger blast radius than this
            // query's own two parameters need. Passing a `DateTime` at midnight and casting explicitly
            // in SQL (`@From::date`) keeps the fix local to this file.
            new
            {
                TenantId = tenantId.Value,
                WorkerId = workerId.Value,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: cancellationToken));

        return [.. rows.Select(ToRow)];
    }

    private static WorkerSlotRow ToRow(WorkerSlotQueryRow row) => new(
        new EventId(row.EventId),
        row.LocalDate,
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        Enum.Parse<EventStatus>(row.Status),
        row.ServiceId is null ? null : new ServiceId(row.ServiceId.Value),
        row.ServiceName,
        row.CustomerId is null ? null : new CustomerId(row.CustomerId.Value),
        row.CustomerDisplayName,
        // null when the query never selected the column at all (SqlWithoutContactData leaves the
        // Dapper-materialised Phone at its type default) - see WorkerSlotRow.Phone's own remarks on
        // the two things a null here can mean, told apart by CustomerId.
        row.Phone is null ? null : new PhoneNumber(row.Phone),
        // `20-18`: null exactly on an Available or Blocked row - see WorkerSlotRow.BookingId's own
        // remarks.
        row.BookingId is null ? null : new EventId(row.BookingId.Value));

    /// <summary>The raw shape Dapper materialises, separate from <see cref="WorkerSlotRow"/> so the
    /// Application-facing row can hold strongly-typed ids and <see cref="DateTimeOffset"/>s while this
    /// one holds what Npgsql hands back.</summary>
    private sealed record WorkerSlotQueryRow(
        Guid EventId,
        DateOnly LocalDate,
        DateTime StartsAt,
        DateTime EndsAt,
        string Status,
        Guid? ServiceId,
        string? ServiceName,
        Guid? CustomerId,
        string? CustomerDisplayName,
        string? Phone,
        Guid? BookingId);
}
