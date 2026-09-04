using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `23-23`'s own instance of it: Dapper over a plain
/// <see cref="NpgsqlDataSource"/>, the same shape <see cref="BookingSurfaceReadStore"/> and
/// <see cref="PendingBookingReadStore"/> already use and for the identical reason - a setup screen
/// has no business inside anybody's write transaction.
///
/// <para>One query, one lateral join per calendar row: <see cref="Sql"/>'s own <c>wf</c> subquery
/// computes, per active worker on that calendar, whether they clear each later fact - service, hours,
/// schedule - and the outer <c>bool_or</c>s fold that per-worker set into "does at least one worker
/// clear this fact", narrowed each time to workers who already cleared the fact before it. See
/// <see cref="IBookingReadinessReadStore"/>'s own remarks for why that is a funnel and not six
/// independent existence checks.</para>
/// </summary>
public sealed class BookingReadinessReadStore(NpgsqlDataSource dataSource) : IBookingReadinessReadStore
{
    /// <summary>
    /// Every column aliased to <see cref="ReadinessRaw"/>'s own parameter names - Dapper binds by
    /// name, not by a snake_case convention, and without the aliases every row silently materialises
    /// empty (the lesson <see cref="PendingBookingReadStore"/>'s own remarks already carry).
    ///
    /// <para><c>wf.worker_id is not null</c> rather than a literal <c>true</c> projected from the
    /// lateral subquery: a <c>left join lateral ... on true</c> still produces exactly one output row
    /// per calendar even when the subquery matches nothing, with every lateral column
    /// <see langword="null"/> - a literal would be <c>true</c> on that placeholder row too, making
    /// "no worker at all" indistinguishable from "a worker exists". Projecting the worker's own id and
    /// testing it for <see langword="null"/> is what tells the two apart.</para>
    ///
    /// <para><c>bool_or</c> ignores <see langword="null"/> inputs, which is exactly what the funnel
    /// needs: a calendar with zero active workers has <c>wf.has_service</c> etc. null on its one
    /// placeholder row, and <c>bool_or</c> over an all-null group is null - <c>coalesce(..., false)</c>
    /// is what turns "no rows cleared this" into the reported <see langword="false"/> rather than a
    /// <see langword="null"/> Dapper cannot bind onto a non-nullable <see langword="bool"/>.</para>
    /// </summary>
    private const string Sql =
        """
        select
            c.id as "CalendarId",
            c.name as "CalendarName",
            c.is_published as "IsPublished",
            coalesce(bool_or(wf.worker_id is not null), false) as "HasWorker",
            coalesce(bool_or(wf.has_service), false) as "HasWorkerWithService",
            coalesce(bool_or(wf.has_service and wf.has_hours), false) as "HasWorkingHours",
            coalesce(bool_or(wf.has_service and wf.has_hours and wf.has_schedule), false) as "HasSchedule",
            exists (
                select 1
                from events e
                where e.calendar_id = c.id
                  and e.status = 'Available'
                  and e.starts_at > @Now
            ) as "HasFutureSlots"
        from calendars c
        left join lateral (
            select
                w.id as worker_id,
                exists (
                    select 1
                    from worker_services ws
                    join services s on s.id = ws.service_id
                    where ws.worker_id = w.id
                ) as has_service,
                (
                    exists (
                        select 1 from working_hours_rules r
                        where r.worker_id = w.id and r.calendar_id = c.id
                    )
                    or exists (
                        select 1 from worker_schedules sc
                        where sc.worker_id = w.id and sc.kind = 'Cycle'
                    )
                ) as has_hours,
                exists (
                    select 1 from worker_schedules sc where sc.worker_id = w.id
                ) as has_schedule
            from workers w
            join calendar_workers cw on cw.worker_id = w.id and cw.calendar_id = c.id
            where w.is_active
        ) wf on true
        where c.tenant_id = @TenantId
        group by c.id, c.name, c.is_published
        order by c.created_at
        """;

    public async Task<IReadOnlyList<CalendarReadinessRow>> GetForTenantAsync(
        TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReadinessRaw>(new CommandDefinition(
            Sql, new { TenantId = tenantId.Value, Now = now }, cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new CalendarReadinessRow(
                new CalendarId(row.CalendarId),
                row.CalendarName,
                row.IsPublished,
                row.HasWorker,
                row.HasWorkerWithService,
                row.HasWorkingHours,
                row.HasSchedule,
                row.HasFutureSlots)),
        ];
    }

    private sealed record ReadinessRaw(
        Guid CalendarId,
        string CalendarName,
        bool IsPublished,
        bool HasWorker,
        bool HasWorkerWithService,
        bool HasWorkingHours,
        bool HasSchedule,
        bool HasFutureSlots);
}
