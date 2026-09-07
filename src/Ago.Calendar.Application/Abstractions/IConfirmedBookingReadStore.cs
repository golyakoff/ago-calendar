using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-34`: what is actually booked, across every calendar a tenant has - the read the pending queue
/// (<see cref="IPendingBookingReadStore"/>) never answers, because it only ever holds what has not
/// been decided yet. A row here is a booking nobody has to act on; it exists to be looked at, not
/// worked.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the identical shape
/// <see cref="IPendingBookingReadStore"/> and <see cref="IContactsReadStore"/> already established:
/// rows shaped for one screen, never <see cref="Event"/> aggregates loaded just to print six
/// columns.</para>
///
/// <para><b>Tenant-scoped, never global</b> - the same isolation story <see cref="IPendingBookingReadStore"/>
/// gives for itself, restated because this is a fourth, independent instance of the same
/// <c>tenant_id</c> predicate rather than a shared base class nobody asked for.</para>
///
/// <para><b>Gated on <see cref="Permission.CustomerRead"/> alone, by the handler - never a two-tier
/// optional join the way the pending queue's phone field is.</b> The item's own scope names the
/// customer as a required column ("readable by day and by master, with the customer and the
/// service"), not an optional bonus one - a caller who may not see who a booking is for cannot
/// usefully read this screen at all, so there is nothing to gain from a caller-without-the-permission
/// still seeing times and services with the customer blanked out the way <c>WorkerSlotRow</c> allows
/// for a free slot. This is the same judgement <c>GetTenantContactsHandler</c> already made for the
/// tenant contacts report, and for the identical reason: both screens exist specifically to show
/// personal data, not to show something else that happens to carry some.</para>
///
/// <para><b>Grouped by booking, not by slot</b> - <c>group by booking_id</c>, the same
/// <c>PendingBookingReadStore</c> shape `20-18` established, restated here rather than copied from
/// <c>WorkerSlotReadStore</c>'s per-slot rows: a multi-slot booking is one appointment, and a list
/// answering "what is on for Thursday" should say so once, not once per slot it happens to
/// occupy.</para>
/// </summary>
public interface IConfirmedBookingReadStore
{
    /// <param name="from">Inclusive, business-local (<see cref="Event.LocalDate"/>'s own zone -
    /// adr/0049).</param>
    /// <param name="to">Inclusive.</param>
    /// <param name="mask">`23-12`'s own gate, the identical shape and reasoning
    /// <see cref="IPendingBookingReadStore.GetPendingForTenantAsync"/>'s own parameter of the same
    /// name carries - resolved once by the handler against the tenant's own rung.</param>
    Task<IReadOnlyList<ConfirmedBookingRow>> GetConfirmedForTenantAsync(
        TenantId tenantId, DateOnly from, DateOnly to, bool mask, CancellationToken cancellationToken);
}

/// <summary>
/// One confirmed booking - an appointment, not a slot. Ordered business-local day first, then the
/// master it belongs to, then start time - the exact order the console groups the screen into
/// (day, then master), so the read store's own ordering is what lets the console build those groups
/// by a single pass rather than a second sort.
/// </summary>
/// <param name="BookingId">The run's own anchor id (<see cref="Event.BookingId"/>), identical to
/// <see cref="PendingBookingRow.BookingId"/>'s own meaning.</param>
/// <param name="CalendarId">Which calendar it belongs to - shown for the same reason
/// <see cref="PendingBookingRow.CalendarId"/> is: a tenant with more than one calendar still needs to
/// know which shop floor a row is on.</param>
/// <param name="WorkerId">Who the visit is with.</param>
/// <param name="WorkerDisplayName">
/// Joined, never gated - a worker's own name is the shop's own roster, not personal data about a
/// customer, the same reasoning <see cref="WorkerSlotRow.ServiceName"/> already gives for a service
/// name. Never null: a worker with any booking history cannot be deleted, only deactivated
/// (`20-13`'s own deletion guard), so a <see cref="EventStatus.Booked"/> row's own worker always still
/// has a row to join to.
/// </param>
/// <param name="ServiceId">What was booked.</param>
/// <param name="ServiceName">Joined, never gated - see <paramref name="WorkerDisplayName"/>'s own
/// reasoning. Nullable only because the join itself is a <c>left join</c>, defensively, the same
/// caution <see cref="WorkerSlotReadStore"/> takes for a foreign key nothing in this product's own
/// rules lets go stale.</param>
/// <param name="CustomerId">Resolves the lead card.</param>
/// <param name="CustomerDisplayName">Null when the customer has never had a name recorded - an
/// optional field on <see cref="Customer"/>, unrelated to this screen's own permission gate (every row
/// here already passed <see cref="Permission.CustomerRead"/>, unlike <see cref="PendingBookingRow.Phone"/>'s
/// two-reasons-for-null story).</param>
/// <param name="StartsAt">The run's first slot.</param>
/// <param name="EndsAt">Exclusive - the run's last slot, buffers included.</param>
/// <param name="LocalDate">The business-local day (adr/0049).</param>
/// <param name="Weekday">0 = Sunday, matching <see cref="DayOfWeek"/> and JavaScript's own
/// <c>Date.prototype.getDay()</c> alike - computed here, server-side, from <paramref name="LocalDate"/>
/// rather than left for the console to derive from a date-only string in the browser's own zone,
/// which is exactly the trap `docs/conventions/date-and-time.md` warns about: parsing a bare
/// <c>YYYY-MM-DD</c> string in JavaScript reads it as UTC midnight, and a browser west of Greenwich
/// would then compute the *previous* calendar day's weekday. The identical reasoning
/// <c>WorkerSlotResponse.Weekday</c> already applies, restated because this is a second, independent
/// place it matters.</param>
/// <param name="Phone">Always populated - masked (see <paramref name="Masked"/>) or real, never null,
/// because every row on this screen already passed <see cref="Permission.CustomerRead"/>
/// (<see cref="IConfirmedBookingReadStore"/>'s own remarks on why this screen has no lesser-included,
/// contact-free state the way the pending queue and the worker-slots screen both do).</param>
/// <param name="Masked">`23-12`. Whether <paramref name="Phone"/> is the tenant's own rung-masked
/// display value rather than the real number - the identical meaning
/// <see cref="ContactRow.Masked"/> and <see cref="PendingBookingRow.Masked"/> already carry.</param>
public readonly record struct ConfirmedBookingRow(
    EventId BookingId,
    CalendarId CalendarId,
    WorkerId WorkerId,
    string WorkerDisplayName,
    ServiceId ServiceId,
    string? ServiceName,
    CustomerId CustomerId,
    string? CustomerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    int Weekday,
    string Phone,
    bool Masked);
