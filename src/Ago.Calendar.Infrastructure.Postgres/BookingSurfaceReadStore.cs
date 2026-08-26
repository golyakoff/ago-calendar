using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side for the public booking surface - Dapper over its own
/// <see cref="NpgsqlDataSource"/>, exactly as <see cref="PendingBookingReadStore"/> does it and for
/// the same reason: a screen has no business inside anybody's write transaction.
///
/// <para>Every column is aliased to its row type's own parameter name. Dapper binds by name and knows
/// nothing about snake_case, and without the aliases every row materialises silently empty - the
/// lesson <see cref="PendingBookingReadStore"/> already carries.</para>
/// </summary>
public sealed class BookingSurfaceReadStore(NpgsqlDataSource dataSource) : IBookingSurfaceReadStore
{
    private const string ServicesSql =
        """
        select distinct s.id as "ServiceId", s.name as "Name", s.duration_minutes as "DurationMinutes"
        from services s
        join worker_services ws on ws.service_id = s.id
        join workers w on w.id = ws.worker_id and w.is_active
        join calendar_workers cw on cw.worker_id = w.id and cw.calendar_id = @CalendarId
        order by s.name
        """;

    private const string WorkersSql =
        """
        select w.id as "WorkerId", w.display_name as "DisplayName"
        from workers w
        join calendar_workers cw on cw.worker_id = w.id and cw.calendar_id = @CalendarId
        join worker_services ws on ws.worker_id = w.id and ws.service_id = @ServiceId
        where w.is_active
        order by w.display_name
        """;

    /// <summary>
    /// Reads through <c>ix_events_available</c>, the partial index `20-01` created on
    /// <c>(calendar_id, starts_at) WHERE status = 'Available'</c> - which is why the status predicate
    /// is written as a literal rather than a parameter: a partial index is only usable when the
    /// planner can prove the query's predicate implies the index's own.
    ///
    /// <para>The duration filter is on the *slot*, not on the service: `20-02` sizes every
    /// materialised slot to the worker's longest offered service, so a shorter service fits a longer
    /// slot and a longer one does not fit a shorter. <c>BookEventHandler</c> asserts the same rule at
    /// claim time; offering a slot here that it would then refuse would be a picker whose choices are
    /// not choices.</para>
    /// </summary>
    private const string OpenSlotsSql =
        """
        select e.id as "EventId", e.worker_id as "WorkerId", w.display_name as "WorkerDisplayName",
               e.starts_at as "StartsAt", e.ends_at as "EndsAt", e.local_date as "LocalDate"
        from events e
        join workers w on w.id = e.worker_id and w.is_active
        join worker_services ws on ws.worker_id = e.worker_id and ws.service_id = @ServiceId
        join services s on s.id = ws.service_id
        where e.calendar_id = @CalendarId
          and e.status = 'Available'
          and e.starts_at > @NotBefore
          and (@WorkerId is null or e.worker_id = @WorkerId)
          and extract(epoch from (e.ends_at - e.starts_at)) >= s.duration_minutes * 60
        order by e.starts_at, w.display_name
        limit @Limit
        """;

    public async Task<IReadOnlyList<BookableServiceRow>> ListServicesAsync(
        CalendarId calendarId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ServiceRaw>(new CommandDefinition(
            ServicesSql, new { CalendarId = calendarId.Value }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => new BookableServiceRow(
            new ServiceId(row.ServiceId), row.Name, row.DurationMinutes))];
    }

    public async Task<IReadOnlyList<BookableWorkerRow>> ListWorkersAsync(
        CalendarId calendarId, ServiceId serviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkerRaw>(new CommandDefinition(
            WorkersSql,
            new { CalendarId = calendarId.Value, ServiceId = serviceId.Value },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => new BookableWorkerRow(new WorkerId(row.WorkerId), row.DisplayName))];
    }

    public async Task<IReadOnlyList<OpenSlotRow>> ListOpenSlotsAsync(
        CalendarId calendarId,
        ServiceId serviceId,
        WorkerId? workerId,
        DateTimeOffset notBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OpenSlotRaw>(new CommandDefinition(
            OpenSlotsSql,
            new
            {
                CalendarId = calendarId.Value,
                ServiceId = serviceId.Value,
                WorkerId = workerId?.Value,
                NotBefore = notBefore,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new OpenSlotRow(
                new EventId(row.EventId),
                new WorkerId(row.WorkerId),
                row.WorkerDisplayName,
                // Npgsql hands a timestamptz back as a DateTime whose Kind is Utc-or-Unspecified
                // depending on the read path; specifying it is what keeps rule 11's "always an
                // explicit offset" true at the boundary rather than only in the aggregate.
                new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
                row.LocalDate)),
        ];
    }

    private sealed record ServiceRaw(Guid ServiceId, string Name, int DurationMinutes);

    private sealed record WorkerRaw(Guid WorkerId, string DisplayName);

    private sealed record OpenSlotRaw(
        Guid EventId, Guid WorkerId, string WorkerDisplayName, DateTime StartsAt, DateTime EndsAt, DateOnly LocalDate);
}
