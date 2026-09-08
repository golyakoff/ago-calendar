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
///
/// <para><b>`23-12`: masking happens here, in the mapping step, never in the SQL and never in the
/// console.</b> The query always reads the real <c>phone</c> column - there is no per-row cost to
/// save the way `20-12`'s own two-SQL-constants trick saves a join, because this store already reads
/// every row's phone unconditionally (a caller without <c>customer:read</c> never reaches this store
/// at all, per <c>GetTenantContactsHandler</c>'s own permission gate) - and <see cref="ToRow"/> is the
/// single place that decides whether the real value or <see cref="PhoneNumber.Masked"/> leaves the
/// store. The real value is never assigned to <see cref="ContactRow.Phone"/> when
/// <c>mask</c> is <see langword="true"/>, so there is no flag downstream of this method that a
/// careless edit could ignore and forward the unmasked number.</para>
///
/// <para><b>`23-60`/`adr/0147`: <c>merged_into_customer_id is null</c> in the query, and duplicate
/// grouping in memory, after.</b> The SQL excludes a tombstoned row outright - <see cref="IContactsReadStore"/>'s
/// own remarks explain why it should not still appear on this screen. Grouping by phone happens after
/// the query returns, over the rows this store already has in hand: a second round trip to ask
/// Postgres "which other rows share this phone" would be answering a question this store's own result
/// set already contains the answer to.</para>
/// </summary>
public sealed class ContactsReadStore(NpgsqlDataSource dataSource) : IContactsReadStore
{
    /// <summary>Newest-first: a tenant reviewing their own contacts most plausibly wants "who have we
    /// heard from lately" at the top, the same ordering choice a lead list in any CRM defaults
    /// to.</summary>
    private const string Sql =
        """
        select id as "CustomerId", phone as "Phone", display_name as "DisplayName", notes as "Notes",
               no_show_count as "NoShowCount", phone_verified_at as "PhoneVerifiedAt",
               operator_confirmed_phone_at as "PhoneConfirmedByOperatorAt",
               first_seen_at as "FirstSeenAt", last_seen_at as "LastSeenAt"
        from customers
        where tenant_id = @TenantId and merged_into_customer_id is null
        order by last_seen_at desc
        """;

    public async Task<IReadOnlyList<ContactRow>> ListForTenantAsync(
        TenantId tenantId, bool mask, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ContactQueryRow>(new CommandDefinition(
            Sql, new { TenantId = tenantId.Value }, cancellationToken: cancellationToken))).ToList();

        // `23-60`: every other live row in this same result set whose phone matches, keyed by the raw
        // (unmasked) value - grouping has to happen before ToRow's own masking, or two rows masked to
        // the identical bulleted display string would look like a match that the real numbers never were.
        var idsByPhone = rows
            .GroupBy(row => row.Phone, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => new CustomerId(row.CustomerId)).ToArray(), StringComparer.Ordinal);

        return [.. rows.Select(row => ToRow(row, mask, idsByPhone[row.Phone]))];
    }

    private static ContactRow ToRow(ContactQueryRow row, bool mask, IReadOnlyList<CustomerId> samePhoneIds) => new(
        new CustomerId(row.CustomerId),
        mask ? new PhoneNumber(row.Phone).Masked() : row.Phone,
        mask,
        row.DisplayName,
        row.Notes,
        row.NoShowCount,
        row.PhoneVerifiedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneVerifiedAt.Value, DateTimeKind.Utc)),
        row.PhoneConfirmedByOperatorAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneConfirmedByOperatorAt.Value, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.FirstSeenAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc)),
        [.. samePhoneIds.Where(id => id.Value != row.CustomerId)]);

    private sealed record ContactQueryRow(
        Guid CustomerId,
        string Phone,
        string? DisplayName,
        string? Notes,
        int NoShowCount,
        DateTime? PhoneVerifiedAt,
        DateTime? PhoneConfirmedByOperatorAt,
        DateTime FirstSeenAt,
        DateTime LastSeenAt);
}
