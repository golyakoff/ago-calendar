using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `26-269`'s own sibling of <see cref="ConfirmedBookingReadStore"/>: Dapper over
/// a plain <see cref="NpgsqlDataSource"/>, its own connection rather than <c>AgoCalendarDbContext</c>'s -
/// restated rather than shared, the identical reasoning every read store in this product already gives
/// for itself.
///
/// <para><b>One query, not two.</b> The identical "no without-contact-data variant" shape
/// <see cref="ConfirmedBookingReadStore"/> already carries and for the same reason: the handler never
/// calls this store at all for a caller lacking <see cref="Permission.CustomerRead"/>
/// (<see cref="IPersonBookingReadStore"/>'s own remarks), so every row this method ever produces already
/// has a customer to join to.</para>
///
/// <para><b>Filtered by person, not by date</b> - the one real difference from its sibling's own SQL:
/// <c>e.person_id = @PersonId</c> replaces the date-window predicate, and <c>e.status in (...)</c>
/// widens from the single <c>'Booked'</c> value to the three held statuses
/// <see cref="IPersonBookingReadStore"/>'s own remarks name.</para>
/// </summary>
public sealed class PersonBookingReadStore(NpgsqlDataSource dataSource) : IPersonBookingReadStore
{
    /// <summary>
    /// The identical join shape <see cref="ConfirmedBookingReadStore"/>'s own remarks explain -
    /// <c>workers</c> an inner join (a booked worker can never be deleted, only deactivated),
    /// <c>services</c> and <c>person_records</c> both defensive <c>left join</c>s - restated here rather
    /// than shared, because a person's own history has no tenant-wide date window to additionally
    /// filter on. <c>group by booking_id</c> and its functionally-dependent columns, including
    /// <c>e.status</c>: every slot of one booking shares the same status by construction (the state
    /// machine writes it once, onto every row of the run, in the same statement), so grouping by it
    /// alongside <c>booking_id</c> is exact, not an aggregation choice - the identical reasoning
    /// <see cref="ConfirmedBookingReadStore"/>'s own remarks give for <c>worker_display_name</c> and
    /// friends.
    /// </summary>
    private const string Sql =
        """
        select e.booking_id as "EventId", e.calendar_id as "CalendarId", e.worker_id as "WorkerId",
               w.display_name as "WorkerDisplayName", e.service_id as "ServiceId", s.name as "ServiceName",
               e.person_id as "PersonId",
               min(e.starts_at) as "StartsAt", max(e.ends_at) as "EndsAt", e.local_date as "LocalDate",
               p.phone as "Phone", e.origin_conversation_id as "OriginConversationId", e.status as "Status"
        from events e
        join workers w on w.id = e.worker_id
        left join services s on s.id = e.service_id
        left join person_records p on p.person_id = e.person_id
        where e.tenant_id = @TenantId
          and e.person_id = @PersonId
          and e.status in ('PendingConfirmation', 'Booked', 'NoShow')
        group by e.booking_id, e.calendar_id, e.worker_id, w.display_name, e.service_id, s.name,
                 e.person_id, e.local_date, p.phone, e.origin_conversation_id, e.status
        order by e.local_date, min(e.starts_at)
        """;

    public async Task<IReadOnlyList<PersonBookingRow>> GetForPersonAsync(
        TenantId tenantId, Guid personId, bool mask, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PersonBookingQueryRow>(new CommandDefinition(
            Sql,
            new { TenantId = tenantId.Value, PersonId = personId },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => ToRow(row, mask))];
    }

    private static PersonBookingRow ToRow(PersonBookingQueryRow row, bool mask) => new(
        new EventId(row.EventId),
        new CalendarId(row.CalendarId),
        new WorkerId(row.WorkerId),
        row.WorkerDisplayName,
        // Non-null on any held-status row - Event.Claim sets it together with the person, and no
        // transition ever clears it. Asserted with `!`, the identical shape
        // ConfirmedBookingReadStore.ToRow already uses for the same two columns.
        new ServiceId(row.ServiceId!.Value),
        row.ServiceName,
        row.PersonId!.Value,
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        row.LocalDate,
        (int)row.LocalDate.DayOfWeek,
        // The real value is never assigned to Phone when mask is true - `23-12`'s own rule, restated
        // the way ConfirmedBookingReadStore.ToRow already gives it.
        mask ? new PhoneNumber(row.Phone!).Masked() : row.Phone!,
        mask,
        row.OriginConversationId,
        Enum.Parse<EventStatus>(row.Status));

    /// <summary>The raw shape Dapper materialises, separate from <see cref="PersonBookingRow"/> so the
    /// Application-facing row can hold strongly-typed ids, a strongly-typed <see cref="EventStatus"/>
    /// and <see cref="DateTimeOffset"/>s while this one holds what Npgsql hands back.</summary>
    private sealed record PersonBookingQueryRow(
        Guid EventId,
        Guid CalendarId,
        Guid WorkerId,
        string WorkerDisplayName,
        Guid? ServiceId,
        string? ServiceName,
        Guid? PersonId,
        DateTime StartsAt,
        DateTime EndsAt,
        DateOnly LocalDate,
        string? Phone,
        Guid? OriginConversationId,
        string Status);
}
