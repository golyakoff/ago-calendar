using Npgsql;

namespace Ago.Calendar.Worker;

/// <summary>`23-12`: `ago-chat`'s own <c>ContactRevealPruneQuery</c> shape, applied to
/// <c>contact_phone_reveals</c> and keyed by <c>occurred_at</c> - the same column name
/// <see cref="Ago.Calendar.Infrastructure.Postgres.ContactPhoneRevealRepository"/> already uses for
/// when the reveal happened. <c>FOR UPDATE SKIP LOCKED</c> costs nothing and keeps this query's shape
/// identical to its sibling, even though nothing else ever updates a reveal record after it is
/// written - a write-once row.</summary>
public static class ContactPhoneRevealPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM contact_phone_reveals
            WHERE id IN (
                SELECT id
                FROM contact_phone_reveals
                WHERE occurred_at < @olderThan
                ORDER BY occurred_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        command.Parameters.AddWithValue("batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
