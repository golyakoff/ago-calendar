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
    /// <summary>
    /// `20-18`: replaces `20-14`'s single-slot filter (<c>wsc.slot_minutes &gt;= s.duration_minutes</c>)
    /// with a "can this ever work" filter, now that a service longer than one slot is several
    /// consecutive slots rather than simply unofferable. Mirrors
    /// <see cref="ConsecutiveRunFinder.ComputeSlotsNeeded"/>'s own arithmetic in SQL - a duplication
    /// this file accepts rather than avoids, because the two run in different engines and this side's
    /// job is only ever "could a run of this length conceivably exist", never the live decision (that
    /// is <see cref="ConsecutiveRunFinder"/>'s own job, in Domain, tested there against the real,
    /// single, authoritative implementation this SQL only approximates). <c>&lt;= 1440</c> - one
    /// day's minutes - is the generous, deliberately
    /// unmeasured bound the item's own scope calls for: a run cannot cross a business-local day
    /// (out of scope), so nothing longer than a day could ever be booked regardless of a worker's own
    /// hours, and checking against the worker's *actual* hours here would turn a "can this ever work"
    /// filter into a live availability query this method was never asked to become.
    ///
    /// <para>Still a <c>LEFT JOIN</c>, for `20-14`'s own original reason: a worker with no schedule row
    /// at all fails open rather than being silently excluded, because in real operation that worker has
    /// no materialised slots to exclude anyway (<c>MaterializeAvailabilityHandler</c> refuses a
    /// schedule-less worker) - this query's job is not to double as an implicit "has a schedule"
    /// gate.</para>
    /// </summary>
    private const string ServicesSql =
        """
        select distinct s.id as "ServiceId", s.name as "Name", s.duration_minutes as "DurationMinutes"
        from services s
        join worker_services ws on ws.service_id = s.id
        join workers w on w.id = ws.worker_id and w.is_active
        join calendar_workers cw on cw.worker_id = w.id and cw.calendar_id = @CalendarId
        left join worker_schedules wsc on wsc.worker_id = w.id
        cross join lateral (
            select (case when wsc.buffers_count_toward_service_duration
                         then ceil((s.duration_minutes + wsc.buffer_minutes)::numeric
                                   / (wsc.slot_minutes + wsc.buffer_minutes))
                         else ceil(s.duration_minutes::numeric / wsc.slot_minutes)
                    end)::int as slots_needed
        ) run
        where wsc.worker_id is null
           or (run.slots_needed * wsc.slot_minutes + (run.slots_needed - 1) * wsc.buffer_minutes) <= 1440
        order by s.name
        """;

    /// <summary>Same "can this ever work" filter as <see cref="ServicesSql"/>, scoped to one already-
    /// chosen service rather than every service on the calendar.</summary>
    private const string WorkersSql =
        """
        select w.id as "WorkerId", w.display_name as "DisplayName"
        from workers w
        join calendar_workers cw on cw.worker_id = w.id and cw.calendar_id = @CalendarId
        join worker_services ws on ws.worker_id = w.id and ws.service_id = @ServiceId
        join services s on s.id = ws.service_id
        left join worker_schedules wsc on wsc.worker_id = w.id
        cross join lateral (
            select (case when wsc.buffers_count_toward_service_duration
                         then ceil((s.duration_minutes + wsc.buffer_minutes)::numeric
                                   / (wsc.slot_minutes + wsc.buffer_minutes))
                         else ceil(s.duration_minutes::numeric / wsc.slot_minutes)
                    end)::int as slots_needed
        ) run
        where w.is_active
          and (wsc.worker_id is null
               or (run.slots_needed * wsc.slot_minutes + (run.slots_needed - 1) * wsc.buffer_minutes) <= 1440)
        order by w.display_name
        """;

    /// <summary>
    /// Reads through <c>ix_events_available</c>, the partial index `20-01` created on
    /// <c>(calendar_id, starts_at) WHERE status = 'Available'</c> - which is why the status predicate
    /// is written as a literal rather than a parameter: a partial index is only usable when the
    /// planner can prove the query's predicate implies the index's own.
    ///
    /// <para><b>`20-18`: a candidate is offered only when a real, unbroken run of the length the
    /// service needs starts there.</b> <c>run.slots_needed</c> is the same arithmetic
    /// <see cref="ConsecutiveRunFinder.ComputeSlotsNeeded"/> computes, evaluated here from the
    /// worker's own <c>worker_schedules</c> row - an <c>INNER JOIN</c> this time, not the fail-open
    /// <c>LEFT JOIN</c> <see cref="ServicesSql"/> uses: a materialised slot cannot exist without a
    /// schedule that produced it, so a worker with none here has no rows to exclude in the first
    /// place, and there is no arithmetic to fail open on. For <c>slots_needed = 1</c> (the ordinary
    /// case) the check is skipped entirely - the single-row form this query has always been. For more,
    /// a successor's expected start is exact arithmetic, not a second lookup of the grid: every slot on
    /// one already-materialised day comes from one <c>WorkerSchedule</c> snapshot applied uniformly
    /// (`20-02`'s materialiser), so the <c>i</c>-th successor of a candidate starting at
    /// <c>e.starts_at</c> must begin at exactly <c>e.starts_at + i * (slot_minutes + buffer_minutes)</c>
    /// if it exists at all - the identical exact-equality reasoning
    /// <see cref="ConsecutiveRunFinder.FindRun"/>'s own remarks state for the same walk done in
    /// memory. This is a courtesy filter, not the guarantee: a slot listed here and taken - whole or in
    /// part - a second later is an ordinary lost race the claim's own <c>WHERE</c> clause (adr/0059,
    /// ADR-0086) settles, not a stale read this query needs to defend against.</para>
    /// </summary>
    private const string OpenSlotsSql =
        """
        select e.id as "EventId", e.worker_id as "WorkerId", w.display_name as "WorkerDisplayName",
               e.starts_at as "StartsAt", e.ends_at as "EndsAt", e.local_date as "LocalDate"
        from events e
        join workers w on w.id = e.worker_id and w.is_active
        join worker_services ws on ws.worker_id = e.worker_id and ws.service_id = @ServiceId
        join services s on s.id = ws.service_id
        join worker_schedules wsc on wsc.worker_id = e.worker_id
        cross join lateral (
            select (case when wsc.buffers_count_toward_service_duration
                         then ceil((s.duration_minutes + wsc.buffer_minutes)::numeric
                                   / (wsc.slot_minutes + wsc.buffer_minutes))
                         else ceil(s.duration_minutes::numeric / wsc.slot_minutes)
                    end)::int as slots_needed
        ) run
        where e.calendar_id = @CalendarId
          and e.status = 'Available'
          and e.starts_at > @NotBefore
          and (@WorkerId is null or e.worker_id = @WorkerId)
          and (
                run.slots_needed = 1
                or not exists (
                    select 1
                    from generate_series(1, run.slots_needed - 1) as successor(i)
                    where not exists (
                        select 1
                        from events e2
                        where e2.calendar_id = e.calendar_id
                          and e2.worker_id = e.worker_id
                          and e2.status = 'Available'
                          and e2.starts_at = e.starts_at
                              + (successor.i * (wsc.slot_minutes + wsc.buffer_minutes)) * interval '1 minute'
                    )
                )
              )
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
