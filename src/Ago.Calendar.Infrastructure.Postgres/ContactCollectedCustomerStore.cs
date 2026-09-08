using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`23-59`'s <see cref="IContactCollectedCustomerStore"/> adapter - a single upsert, the same
/// "one statement, Postgres arbitrates the conflict" shape <c>BookingStore</c>'s own
/// <c>UpsertCustomerSql</c> uses, for the identical reason: a read-then-insert-with-retry would have a
/// window a concurrent redelivery could walk through.</summary>
public sealed class ContactCollectedCustomerStore(AgoCalendarDbContext db, IIdGenerator idGenerator) : IContactCollectedCustomerStore
{
    /// <summary>
    /// <c>source_contact_id</c> is the conflict target, not <c>phone</c> - see
    /// <see cref="Customer.SourceContactId"/>'s own remarks for why the phone is never the dedup key on
    /// this path: two distinct chat contacts sharing a phone must still become two rows.
    /// <c>ON CONFLICT ... WHERE source_contact_id IS NOT NULL</c> matches
    /// <c>ux_customers_tenant_source_contact</c>'s own partial predicate exactly, the identical
    /// "the statement's WHERE clause has to match the index's" requirement <c>BookingStore</c>'s own
    /// remarks state for its own upsert.
    ///
    /// <para><c>last_seen_at</c> takes the greater of the two values, the same "a redelivery that
    /// arrives out of order must not rewind the watermark" rule <c>BookingStore</c>'s own SQL already
    /// applies. Nothing else is ever updated on conflict - <c>phone</c>, <c>first_seen_at</c> and
    /// <c>source</c> are the original contact's own facts, and a chat contact detail is never edited
    /// once recorded (`Ago.Chat.Domain.VisitorContactDetail`'s own remarks), so there is nothing for a
    /// second delivery to have changed.</para>
    /// </summary>
    private const string UpsertSql =
        """
        INSERT INTO customers (id, tenant_id, phone, source, source_contact_id, no_show_count, first_seen_at, last_seen_at)
        VALUES ({0}, {1}, {2}, 'Chat', {3}, 0, {4}, {4})
        ON CONFLICT (tenant_id, source_contact_id) WHERE source_contact_id IS NOT NULL DO UPDATE
            SET last_seen_at = GREATEST(customers.last_seen_at, EXCLUDED.last_seen_at)
        """;

    public Task UpsertAsync(
        TenantId tenantId, Guid sourceContactId, PhoneNumber phone, DateTimeOffset recordedAt,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [idGenerator.NewId(recordedAt), tenantId.Value, phone.Value, sourceContactId, recordedAt],
            cancellationToken);
}
