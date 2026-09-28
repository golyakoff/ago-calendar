using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-269`: one person's own bookings, past and upcoming together - the read the client-detail hub
/// needs and the tenant-wide <see cref="IConfirmedBookingReadStore"/> cannot answer, because that one
/// takes a date window over *everyone* and only ever shows <see cref="EventStatus.Booked"/>. This is
/// its sibling, filtered by person instead of by date, and widened to the three statuses a person's own
/// history is made of.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the identical shape every other read store in
/// this product already establishes: rows shaped for one screen, never an <see cref="Event"/> aggregate
/// loaded just to print a few columns.</para>
///
/// <para><b>Held statuses only - <see cref="EventStatus.PendingConfirmation"/>, <see cref="EventStatus.Booked"/>
/// and <see cref="EventStatus.NoShow"/> - never <see cref="EventStatus.Cancelled"/> or
/// <see cref="EventStatus.Available"/>.</b> This is the exact set `docs/backlog/26-269-clients-redesign.md`
/// §8 item 1 names for this read. A withdrawn booking is not part of the history the design's
/// Предстоящие/Прошедшие split exists to show - the design's own Прошедшие section calls out only the
/// no-show marker, never a cancelled one - and an <see cref="EventStatus.Available"/> row belongs to
/// nobody yet, so it cannot be "this person's" in the first place.</para>
///
/// <para><b>No date window, no mask parameter decided elsewhere but the same gate.</b> Unlike
/// <see cref="IConfirmedBookingReadStore.GetConfirmedForTenantAsync"/>, there is no <c>from</c>/<c>to</c>
/// to validate - a person's whole held history is the point, since the caller (the client-detail hub)
/// is the one place that needs both the future and the past at once. <paramref name="mask"/> below is
/// still resolved once by the handler against the tenant's own contact-visibility rung, the identical
/// split every other read store in this product already uses.</para>
///
/// <para><b>Tenant-scoped by construction, not by a lookup.</b> The <c>events</c> table already carries
/// <c>tenant_id</c> and <c>person_id</c> together on every row, so filtering on both at once is what
/// makes a person id from another tenant, or one that never existed, come back as an honest empty list
/// rather than a row it has no right to see - the same "wrong tenant reads like no such row" shape
/// <see cref="ConfirmedBookingRow"/>'s own tenant predicate already gives, without a separate
/// <see cref="IPersonRecordRepository"/> existence check this read has no other reason to make.</para>
/// </summary>
public interface IPersonBookingReadStore
{
    /// <param name="mask">`23-12`'s own gate, the identical shape and reasoning
    /// <see cref="IConfirmedBookingReadStore.GetConfirmedForTenantAsync"/>'s own parameter of the same
    /// name carries - resolved once by the handler against the tenant's own rung.</param>
    Task<IReadOnlyList<PersonBookingRow>> GetForPersonAsync(
        TenantId tenantId, Guid personId, bool mask, CancellationToken cancellationToken);
}

/// <summary>
/// One of this person's own bookings - an appointment, not a slot, the same one-row-per-<c>booking_id</c>
/// shape <see cref="ConfirmedBookingRow"/> already established. Ordered chronologically, oldest first;
/// splitting into Предстоящие/Прошедшие against "now" is the client's own job (a display decision, not
/// a read-store one - the same reasoning that keeps <see cref="Status"/> a plain enum here rather than
/// a pre-computed "isPast" flag this store would have to know the current instant to produce).
/// </summary>
/// <param name="BookingId">The run's own anchor id, identical to
/// <see cref="ConfirmedBookingRow.BookingId"/>'s own meaning.</param>
/// <param name="CalendarId">Which calendar it belongs to - see <see cref="ConfirmedBookingRow.CalendarId"/>.</param>
/// <param name="WorkerId">Who the visit is with.</param>
/// <param name="WorkerDisplayName">Joined, never gated - see <see cref="ConfirmedBookingRow.WorkerDisplayName"/>'s
/// own reasoning.</param>
/// <param name="ServiceId">What was booked.</param>
/// <param name="ServiceName">Joined, never gated, nullable only because the join is a defensive
/// <c>left join</c> - see <see cref="ConfirmedBookingRow.ServiceName"/>.</param>
/// <param name="PersonId">`adr/0184`: the opaque person id, echoed back verbatim - the caller already
/// named it in the query, but carrying it on every row keeps this shape identical to
/// <see cref="ConfirmedBookingRow"/>'s rather than a special case with one field missing.</param>
/// <param name="StartsAt">The run's first slot.</param>
/// <param name="EndsAt">Exclusive - the run's last slot, buffers included.</param>
/// <param name="LocalDate">The business-local day (adr/0049).</param>
/// <param name="Weekday">0 = Sunday, computed server-side - see <see cref="ConfirmedBookingRow.Weekday"/>'s
/// own remarks on why this is never left for the console/Android to derive from a bare date string.</param>
/// <param name="Phone">Always populated - masked or real, never null, because every row on this screen
/// already passed <see cref="Permission.CustomerRead"/>, the identical reasoning
/// <see cref="ConfirmedBookingRow.Phone"/> gives for itself.</param>
/// <param name="Masked">`23-12`. Whether <paramref name="Phone"/> is the tenant's own rung-masked display
/// value - see <see cref="ConfirmedBookingRow.Masked"/>.</param>
/// <param name="OriginConversationId">`26-121`/`26-136`/`adr/0184`: the chat conversation this booking
/// came in through, or <see langword="null"/> for a booking with no chat origin - carried verbatim, the
/// identical meaning <see cref="ConfirmedBookingRow.OriginConversationId"/> already has.</param>
/// <param name="Status">`26-269`: the one field <see cref="ConfirmedBookingRow"/> does not need and this
/// read does - a tenant-wide confirmed-bookings screen only ever shows <see cref="EventStatus.Booked"/>
/// rows, so it has nothing to distinguish; a person's own history mixes <see cref="EventStatus.Booked"/>,
/// <see cref="EventStatus.PendingConfirmation"/> and <see cref="EventStatus.NoShow"/> rows together, and
/// the client-detail hub's Прошедшие segment needs this to draw the no-show marker (`docs/backlog/26-269-clients-redesign.md`
/// §4).</param>
public readonly record struct PersonBookingRow(
    EventId BookingId,
    CalendarId CalendarId,
    WorkerId WorkerId,
    string WorkerDisplayName,
    ServiceId ServiceId,
    string? ServiceName,
    Guid PersonId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    int Weekday,
    string Phone,
    bool Masked,
    Guid? OriginConversationId,
    EventStatus Status);
