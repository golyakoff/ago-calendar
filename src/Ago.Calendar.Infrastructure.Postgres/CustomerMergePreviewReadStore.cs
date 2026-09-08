using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `23-60`/`adr/0147`: <see cref="ICustomerMergePreviewReadStore"/>'s own remarks explain the shape -
/// one query for both candidates, tenant-scoped in the same statement that names them, the identical
/// "one query, not two" discipline `ConfirmedBookingReadStore`'s own doc comment states for a
/// different reason.
/// </summary>
public sealed class CustomerMergePreviewReadStore(NpgsqlDataSource dataSource) : ICustomerMergePreviewReadStore
{
    /// <summary>
    /// Every status, unlike <c>ConfirmedBookingReadStore</c>'s own <c>status = 'Booked'</c> filter -
    /// <see cref="ICustomerMergePreviewReadStore"/>'s own remarks explain why a cancellation or a
    /// no-show belongs on this particular screen. <c>customer_id in (@First, @Second)</c> is what
    /// excludes <see cref="EventStatus.Available"/>/<see cref="EventStatus.Blocked"/> rows without a
    /// separate predicate - neither ever carries a customer, so neither can match either id.
    /// <c>group by booking_id</c>, the identical multi-slot-run collapse <c>ConfirmedBookingReadStore</c>
    /// already establishes, for the same reason: a three-slot appointment is one row on this screen,
    /// not three.
    /// </summary>
    private const string Sql =
        """
        select e.booking_id as "EventId", e.customer_id as "CustomerId", e.status as "Status",
               s.name as "ServiceName", w.display_name as "WorkerDisplayName",
               min(e.starts_at) as "StartsAt", max(e.ends_at) as "EndsAt", e.local_date as "LocalDate"
        from events e
        join workers w on w.id = e.worker_id
        left join services s on s.id = e.service_id
        where e.tenant_id = @TenantId and e.customer_id in (@First, @Second)
        group by e.booking_id, e.customer_id, e.status, s.name, w.display_name, e.local_date
        order by min(e.starts_at)
        """;

    public async Task<IReadOnlyList<CustomerMergePreviewBookingRow>> ListForCandidatesAsync(
        TenantId tenantId, CustomerId firstCustomerId, CustomerId secondCustomerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PreviewQueryRow>(new CommandDefinition(
            Sql,
            new { TenantId = tenantId.Value, First = firstCustomerId.Value, Second = secondCustomerId.Value },
            cancellationToken: cancellationToken));

        return [.. rows.Select(ToRow)];
    }

    private static CustomerMergePreviewBookingRow ToRow(PreviewQueryRow row) => new(
        new CustomerId(row.CustomerId),
        new EventId(row.EventId),
        Enum.Parse<EventStatus>(row.Status),
        row.ServiceName,
        row.WorkerDisplayName,
        new DateTimeOffset(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
        row.LocalDate);

    private sealed record PreviewQueryRow(
        Guid EventId,
        Guid CustomerId,
        string Status,
        string? ServiceName,
        string WorkerDisplayName,
        DateTime StartsAt,
        DateTime EndsAt,
        DateOnly LocalDate);
}
