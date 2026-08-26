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
    private const string Sql =
        """
        select id as "EventId", calendar_id as "CalendarId", worker_id as "WorkerId",
               service_id as "ServiceId", customer_id as "CustomerId",
               starts_at as "StartsAt", ends_at as "EndsAt", local_date as "LocalDate",
               confirmation_deadline as "ConfirmationDeadline",
               (confirmation_deadline <= @Now) as "IsOverdue"
        from events
        where tenant_id = @TenantId
          and status = 'PendingConfirmation'
        order by confirmation_deadline
        limit @Limit
        """;

    public async Task<IReadOnlyList<PendingBookingRow>> GetPendingForTenantAsync(
        TenantId tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingBookingQueueRow>(new CommandDefinition(
            Sql,
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
        row.IsOverdue);

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
        bool IsOverdue);
}
