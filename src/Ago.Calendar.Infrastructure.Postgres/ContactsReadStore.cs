using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `20-12`'s own: Dapper over a plain <see cref="NpgsqlDataSource"/>, its own
/// connection rather than <c>AgoCalendarDbContext</c>'s - the identical reasoning
/// <c>PendingBookingReadStore</c>'s own remarks give, restated because this is a second, independent
/// instance of the same call rather than a shared base class nobody asked for.
/// </summary>
public sealed class ContactsReadStore(NpgsqlDataSource dataSource) : IContactsReadStore
{
    /// <summary>Newest-first: a tenant reviewing their own contacts most plausibly wants "who have we
    /// heard from lately" at the top, the same ordering choice a lead list in any CRM defaults
    /// to.</summary>
    private const string Sql =
        """
        select id as "CustomerId", phone as "Phone", display_name as "DisplayName", notes as "Notes",
               no_show_count as "NoShowCount", first_seen_at as "FirstSeenAt", last_seen_at as "LastSeenAt"
        from customers
        where tenant_id = @TenantId
        order by last_seen_at desc
        """;

    public async Task<IReadOnlyList<ContactRow>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContactQueryRow>(new CommandDefinition(
            Sql, new { TenantId = tenantId.Value }, cancellationToken: cancellationToken));

        return [.. rows.Select(ToRow)];
    }

    private static ContactRow ToRow(ContactQueryRow row) => new(
        new CustomerId(row.CustomerId),
        new PhoneNumber(row.Phone),
        row.DisplayName,
        row.Notes,
        row.NoShowCount,
        new DateTimeOffset(DateTime.SpecifyKind(row.FirstSeenAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc)));

    private sealed record ContactQueryRow(
        Guid CustomerId,
        string Phone,
        string? DisplayName,
        string? Notes,
        int NoShowCount,
        DateTime FirstSeenAt,
        DateTime LastSeenAt);
}
