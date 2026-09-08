using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `23-60`/`adr/0147`: raw Npgsql, not EF - <see cref="ICustomerMergeReadStore"/>'s own remarks give
/// the reason, the identical shape <c>ContactPhoneRevealRepository</c> already establishes for its
/// own audit trail.
/// </summary>
public sealed class CustomerMergeReadStore(NpgsqlDataSource dataSource) : ICustomerMergeReadStore
{
    public async Task<CustomerMergePage> ListForTenantAsync(
        TenantId tenantId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, merged_at, survivor_customer_id, absorbed_customer_id, operator_id, bookings_moved
            from customer_merges
            where tenant_id = @tenantId and (@beforeId is null or id < @beforeId)
            order by id desc
            limit @limit
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid)
        {
            Value = (object?)beforeId ?? DBNull.Value,
        });
        // `ContactPhoneRevealRepository`'s own "ask for limit+1" shape - paging without a second
        // count query.
        command.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CustomerMergeRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CustomerMergeRecord(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetInt32(5)));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveRange(limit, items.Count - limit);
        }

        var nextBeforeId = hasMore ? items[^1].Id : (Guid?)null;

        return new CustomerMergePage(items, nextBeforeId);
    }
}
