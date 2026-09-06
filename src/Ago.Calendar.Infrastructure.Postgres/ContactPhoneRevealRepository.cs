using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `23-12`: raw Npgsql, not EF - <see cref="IContactPhoneRevealRepository"/>'s own remarks explain why
/// (no aggregate, no invariant beyond "one row per event"), the identical shape `ago-chat`'s own
/// <c>ContactRevealRepository</c> already establishes for the account-side twin of this table.
/// </summary>
public sealed class ContactPhoneRevealRepository(NpgsqlDataSource dataSource) : IContactPhoneRevealRepository
{
    public async Task RecordAsync(ContactPhoneRevealToWrite reveal, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into contact_phone_reveals (id, occurred_at, tenant_id, customer_id, operator_id, surface)
            values (@id, @occurredAt, @tenantId, @customerId, @operatorId, @surface)
            """,
            connection);
        command.Parameters.AddWithValue("id", reveal.Id);
        command.Parameters.AddWithValue("occurredAt", reveal.OccurredAt);
        command.Parameters.AddWithValue("tenantId", reveal.TenantId.Value);
        command.Parameters.AddWithValue("customerId", reveal.CustomerId.Value);
        command.Parameters.AddWithValue("operatorId", reveal.OperatorId.Value);
        command.Parameters.AddWithValue("surface", reveal.Surface);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ContactPhoneRevealPage> ListForTenantAsync(
        TenantId tenantId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, occurred_at, customer_id, operator_id, surface
            from contact_phone_reveals
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
        // One extra row, not returned - the same "ask for limit+1" shape `ago-chat`'s own
        // ContactRevealRepository uses, so paging needs no separate count query.
        command.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ContactPhoneRevealItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ContactPhoneRevealItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4)));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveRange(limit, items.Count - limit);
        }

        var nextBeforeId = hasMore ? items[^1].Id : (Guid?)null;

        return new ContactPhoneRevealPage(items, nextBeforeId);
    }
}
