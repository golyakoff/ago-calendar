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
/// console.</b> The query always reads the real <c>phone</c> column - a caller without
/// <c>customer:read</c> never reaches this store at all, per <c>GetTenantContactsHandler</c>'s own
/// permission gate - and <see cref="ToRow"/> is the single place that decides whether the real value
/// or <see cref="PhoneNumber.Masked"/> leaves the store. The real value is never assigned to
/// <see cref="ContactRow.Phone"/> when <c>mask</c> is <see langword="true"/>, so there is no flag
/// downstream of this method that a careless edit could ignore and forward the unmasked number.</para>
///
/// <para><b>`adr/0184`: reads <c>person_records</c>, and nothing about the person's name or notes.</b>
/// Those are chat's; the console fetches them by <see cref="ContactRow.PersonId"/> and merges them
/// onto these rows for display. The `23-60` tombstone filter and phone-duplicate grouping this store
/// used to do are gone with the merge (author decision O2).</para>
/// </summary>
public sealed class ContactsReadStore(NpgsqlDataSource dataSource) : IContactsReadStore
{
    /// <summary>Newest-first: a tenant reviewing their own contacts most plausibly wants "who have we
    /// heard from lately" at the top, the same ordering choice a lead list in any CRM defaults
    /// to.
    ///
    /// <para>`26-282`: one added aggregate, the identical <c>left join events</c> +
    /// <c>count(distinct booking_id) filter (...)</c> shape <c>PersonRecognitionReadStore</c>'s own
    /// <c>BookingCount</c> already uses, narrowed from "held" to "held and still ahead of <c>@Now</c>" -
    /// see <see cref="ContactRow.UpcomingBookingCount"/>'s own remarks for why <c>NoShow</c> is left out
    /// of the status list here.</para></summary>
    private const string Sql =
        """
        select p.person_id as "PersonId", p.phone as "Phone",
               p.no_show_count as "NoShowCount", p.phone_verified_at as "PhoneVerifiedAt",
               p.operator_confirmed_phone_at as "PhoneConfirmedByOperatorAt",
               p.first_seen_at as "FirstSeenAt", p.last_seen_at as "LastSeenAt",
               count(distinct e.booking_id) filter (
                   where e.status in ('PendingConfirmation', 'Booked') and e.starts_at > @Now
               ) as "UpcomingBookingCount"
        from person_records p
        left join events e on e.person_id = p.person_id and e.tenant_id = p.tenant_id
        where p.tenant_id = @TenantId
        group by p.person_id, p.phone, p.no_show_count, p.phone_verified_at, p.operator_confirmed_phone_at,
                 p.first_seen_at, p.last_seen_at
        order by p.last_seen_at desc
        """;

    public async Task<IReadOnlyList<ContactRow>> ListForTenantAsync(
        TenantId tenantId, bool mask, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContactQueryRow>(new CommandDefinition(
            Sql, new { TenantId = tenantId.Value, Now = now }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => ToRow(row, mask))];
    }

    private static ContactRow ToRow(ContactQueryRow row, bool mask) => new(
        row.PersonId,
        mask ? new PhoneNumber(row.Phone).Masked() : row.Phone,
        mask,
        row.NoShowCount,
        row.PhoneVerifiedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneVerifiedAt.Value, DateTimeKind.Utc)),
        row.PhoneConfirmedByOperatorAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.PhoneConfirmedByOperatorAt.Value, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.FirstSeenAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc)),
        (int)row.UpcomingBookingCount);

    private sealed record ContactQueryRow(
        Guid PersonId,
        string Phone,
        int NoShowCount,
        DateTime? PhoneVerifiedAt,
        DateTime? PhoneConfirmedByOperatorAt,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        long UpcomingBookingCount);
}
