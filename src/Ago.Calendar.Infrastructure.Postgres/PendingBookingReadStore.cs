using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, and this product's first instance of it: Dapper over a plain
/// <see cref="NpgsqlDataSource"/>, returning rows rather than aggregates.
///
/// <para>Its own connection rather than the <c>AgoCalendarDbContext</c>'s, which is the shape
/// ago-chat's <c>ConversationReadStore</c> settled on: a read model that shares a write context
/// inherits that context's change tracker and its ambient transaction, and a queue screen has no
/// business being inside anybody's write transaction.</para>
/// </summary>
public sealed class PendingBookingReadStore(NpgsqlDataSource dataSource) : IPendingBookingReadStore
{
    /// <summary>
    /// Aliased to the row type's own parameter names - Dapper binds by name, not by a
    /// snake_case-to-PascalCase convention, and without the aliases every row silently fails to
    /// materialise (a lesson ago-chat's own read store learned by running it against a real
    /// Postgres).
    ///
    /// <para>Reads through <c>ix_events_pending_confirmation</c>, the partial index `20-01` created
    /// on <c>(tenant_id, confirmation_deadline) WHERE status = 'PendingConfirmation'</c> - the same
    /// index the sweep's own claim uses, which is why it is one index and not two: `20-01`'s comment
    /// predicted both readers.</para>
    ///
    /// <para><c>is_overdue</c> is computed in SQL from the caller's <c>@now</c> rather than in C#
    /// after the fact, so the whole row arrives already answering the one question that matters about
    /// the sweep's health.</para>
    /// </summary>
    /// <summary>
    /// A literal <c>null::text as "Phone"</c> keeps this query's own row shape identical to
    /// <see cref="SqlWithContactData"/>'s, at no join cost: Dapper's constructor-matching
    /// materialisation needs every query to produce the same columns as
    /// <see cref="PendingBookingQueueRow"/>'s single generated constructor, since a C# default
    /// parameter value is a caller-side convenience only - it does not create a second, shorter
    /// constructor for Dapper's reflection to find. Found by running this query for real: the first
    /// attempt omitted the column entirely and Dapper threw
    /// "a parameterless default constructor or one matching signature... is required" at
    /// materialisation, not at compile time.
    /// </summary>
    private const string SqlWithoutContactData =
        """
        select id as "EventId", calendar_id as "CalendarId", worker_id as "WorkerId",
               service_id as "ServiceId", customer_id as "CustomerId",
               starts_at as "StartsAt", ends_at as "EndsAt", local_date as "LocalDate",
               confirmation_deadline as "ConfirmationDeadline",
               (confirmation_deadline <= @Now) as "IsOverdue",
               null::text as "Phone"
        from events
        where tenant_id = @TenantId
          and status = 'PendingConfirmation'
        order by confirmation_deadline
        limit @Limit
        """;

    /// <summary>
    /// `20-12`: the only difference from <see cref="SqlWithoutContactData"/> is this join and its one
    /// extra column - chosen over always joining and hiding the result, so a caller without
    /// <c>customer:read</c> costs the database nothing extra, exactly as `20-04`'s original read store
    /// intended. <c>left join</c> rather than an inner join: <c>events.customer_id</c> is trusted to
    /// resolve (see <see cref="ToRow"/>'s own remark on why it is asserted with <c>!</c>), but a join
    /// condition is cheap insurance against ever turning a data anomaly into a row silently dropped
    /// from an operator's queue.
    /// </summary>
    private const string SqlWithContactData =
        """
        select e.id as "EventId", e.calendar_id as "CalendarId", e.worker_id as "WorkerId",
               e.service_id as "ServiceId", e.customer_id as "CustomerId",
               e.starts_at as "StartsAt", e.ends_at as "EndsAt", e.local_date as "LocalDate",
               e.confirmation_deadline as "ConfirmationDeadline",
               (e.confirmation_deadline <= @Now) as "IsOverdue",
               c.phone as "Phone"
        from events e
        left join customers c on c.id = e.customer_id
        where e.tenant_id = @TenantId
          and e.status = 'PendingConfirmation'
        order by e.confirmation_deadline
        limit @Limit
        """;

    public async Task<IReadOnlyList<PendingBookingRow>> GetPendingForTenantAsync(
        TenantId tenantId, DateTimeOffset now, int limit, bool includeContactData, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingBookingQueueRow>(new CommandDefinition(
            includeContactData ? SqlWithContactData : SqlWithoutContactData,
            new { TenantId = tenantId.Value, Now = now, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows.Select(ToRow)];
    }

    private static PendingBookingRow ToRow(PendingBookingQueueRow row) => new(
        new EventId(row.EventId),
        new CalendarId(row.CalendarId),
        new WorkerId(row.WorkerId),
        // Both are non-null on any PendingConfirmation row - Event.Claim sets them together with the
        // status, and no transition ever clears them. Asserted with `!` rather than defended with a
        // fallback: a null here would mean the state machine had been bypassed, and inventing an
        // empty id would hide that instead of surfacing it.
        new ServiceId(row.ServiceId!.Value),
        new CustomerId(row.CustomerId!.Value),
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        row.LocalDate,
        new DateTimeOffset(DateTime.SpecifyKind(row.ConfirmationDeadline!.Value, DateTimeKind.Utc)),
        row.IsOverdue,
        // null when the query never selected the column at all (SqlWithoutContactData leaves the
        // Dapper-materialised Phone at its type default) - see PendingBookingRow.Phone's own remarks
        // on why that is the only state a null here can mean.
        row.Phone is null ? null : new PhoneNumber(row.Phone));

    /// <summary>
    /// The raw shape Dapper materialises, separate from <see cref="PendingBookingRow"/> so the
    /// Application-facing row can hold strongly-typed ids and <see cref="DateTimeOffset"/>s while
    /// this one holds what Npgsql hands back. Its nullable columns are nullable because the *column*
    /// is, not because a pending booking could be missing them.
    /// </summary>
    private sealed record PendingBookingQueueRow(
        Guid EventId,
        Guid CalendarId,
        Guid WorkerId,
        Guid? ServiceId,
        Guid? CustomerId,
        DateTime StartsAt,
        DateTime EndsAt,
        DateOnly LocalDate,
        DateTime? ConfirmationDeadline,
        bool IsOverdue,
        string? Phone);
}
