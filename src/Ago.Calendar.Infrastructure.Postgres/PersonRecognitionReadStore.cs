using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `26-268`§2a/`adr/0188`'s own: Dapper over the shared <see cref="NpgsqlDataSource"/>,
/// the identical "its own connection, never the write context" reasoning <see cref="ContactsReadStore"/>
/// and <see cref="PersonBookingReadStore"/> already give for themselves.
///
/// <para><b>One query, every candidate - no attempt to pick "the" one.</b> This is the SQL-level
/// statement of <see cref="IPersonRecognitionReadStore"/>'s own point: <c>where phone = @Phone</c> can
/// legitimately return zero, one, or several rows (`adr/0147`: two people can share a number), and this
/// store makes no attempt to collapse them - collapsing would be the auto-merge `adr/0188` explicitly
/// refuses.</para>
///
/// <para><b>The booking count is one cheap aggregate, not a second round trip.</b> `docs/backlog/26-268-manual-booking-entry.md`
/// §3.4's "returning client" hint («Постоянный клиент · 3 записи») only needs a count, not a booking's
/// own detail - so this joins <c>events</c> once per candidate and counts distinct bookings in the same
/// held-status set <see cref="IPersonBookingReadStore"/>'s own remarks name
/// (<see cref="EventStatus.PendingConfirmation"/>, <see cref="EventStatus.Booked"/>,
/// <see cref="EventStatus.NoShow"/>), rather than calling that store once per candidate.</para>
///
/// <para><b>Masking happens here, in the mapping step - the identical discipline <see cref="ContactsReadStore"/>'s
/// own remarks state.</b> The query always reads the real <c>phone</c> column (a caller without
/// <c>customer:read</c> never reaches this store at all); <see cref="ToRow"/> is the one place that
/// decides whether the real value or <see cref="PhoneNumber.Masked"/> leaves the store.</para>
/// </summary>
public sealed class PersonRecognitionReadStore(NpgsqlDataSource dataSource) : IPersonRecognitionReadStore
{
    /// <summary>Newest-first, the identical ordering choice <see cref="ContactsReadStore"/> already
    /// makes for the same reason: a returning client most plausibly means whoever booked most recently.
    /// <c>ix_person_records_tenant_phone</c> (<c>PersonRecordConfiguration</c>) serves the
    /// <c>tenant_id</c>/<c>phone</c> predicate directly.</summary>
    private const string Sql =
        """
        select p.person_id as "PersonId", p.phone as "Phone", p.no_show_count as "NoShowCount",
               p.phone_verified_at as "PhoneVerifiedAt", p.operator_confirmed_phone_at as "PhoneConfirmedByOperatorAt",
               p.first_seen_at as "FirstSeenAt", p.last_seen_at as "LastSeenAt",
               count(distinct e.booking_id) filter (
                   where e.status in ('PendingConfirmation', 'Booked', 'NoShow')) as "BookingCount"
        from person_records p
        left join events e on e.person_id = p.person_id and e.tenant_id = p.tenant_id
        where p.tenant_id = @TenantId and p.phone = @Phone
        group by p.person_id, p.phone, p.no_show_count, p.phone_verified_at, p.operator_confirmed_phone_at,
                 p.first_seen_at, p.last_seen_at
        order by p.last_seen_at desc
        """;

    public async Task<IReadOnlyList<PersonRecognitionCandidateRow>> FindByPhoneAsync(
        TenantId tenantId, PhoneNumber phone, bool mask, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PersonRecognitionQueryRow>(new CommandDefinition(
            Sql,
            new { TenantId = tenantId.Value, Phone = phone.Value },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => ToRow(row, mask))];
    }

    private static PersonRecognitionCandidateRow ToRow(PersonRecognitionQueryRow row, bool mask) => new(
        row.PersonId,
        mask ? new PhoneNumber(row.Phone).Masked() : row.Phone,
        mask,
        row.NoShowCount,
        (int)row.BookingCount,
        row.PhoneVerifiedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneVerifiedAt.Value, DateTimeKind.Utc)),
        row.PhoneConfirmedByOperatorAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneConfirmedByOperatorAt.Value, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.FirstSeenAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc)));

    /// <summary>The raw shape Dapper materialises - the identical split <see cref="ContactsReadStore"/>'s
    /// own <c>ContactQueryRow</c> uses, so the Application-facing row can hold real
    /// <see cref="DateTimeOffset"/>s while this one holds what Npgsql hands back for a <c>timestamptz</c>
    /// read without a registered mapping.</summary>
    private sealed record PersonRecognitionQueryRow(
        Guid PersonId,
        string Phone,
        int NoShowCount,
        DateTime? PhoneVerifiedAt,
        DateTime? PhoneConfirmedByOperatorAt,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        long BookingCount);
}
