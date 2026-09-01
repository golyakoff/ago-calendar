using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `20-15`: what the materialiser (`20-02`) and the booking lifecycle (`20-03`/`20-04`) actually
/// produced for one worker, over a date range - the tenant's own view of their week, which today only
/// exists from the customer's side of the public widget.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the same shape
/// <see cref="IPendingBookingReadStore"/> and <see cref="IContactsReadStore"/> already established:
/// rows shaped for a screen, never <see cref="Event"/> aggregates with their own state-machine
/// invariants loaded just to print six columns.</para>
///
/// <para><b>Two SQL constants, not one query that always joins and then masks - copied from
/// <see cref="IPendingBookingReadStore"/> rather than invented fresh.</b> `20-12`'s own argument
/// applies unchanged: a caller without <see cref="Permission.CustomerRead"/> must cost the database
/// nothing extra, not merely see a hidden column. See
/// <c>Ago.Calendar.Infrastructure.Postgres.WorkerSlotReadStore</c> for the two constants themselves.
/// </para>
///
/// <para><b>Every status, not just the occupied ones.</b> The item exists to answer "did my schedule
/// come out right", and an <see cref="EventStatus.Available"/> row nobody has claimed is as much a
/// part of that answer as a <see cref="EventStatus.Booked"/> one - a Tuesday with no rows at all and a
/// Tuesday with rows that are all still <c>Available</c> look identical from the widget's side and are
/// two different problems from this screen's.</para>
/// </summary>
public interface IWorkerSlotReadStore
{
    /// <param name="from">Inclusive, business-local (<see cref="Event.LocalDate"/>'s own zone).</param>
    /// <param name="to">Inclusive.</param>
    /// <param name="includeContactData">`20-12`'s own gate, decided once by
    /// <c>GetWorkerSlotsHandler</c> and handed down rather than re-checked here - see
    /// <see cref="IPendingBookingReadStore.GetPendingForTenantAsync"/>'s own parameter of the same
    /// name for why that split exists.</param>
    Task<IReadOnlyList<WorkerSlotRow>> GetForWorkerAsync(
        TenantId tenantId, WorkerId workerId, DateOnly from, DateOnly to, bool includeContactData,
        CancellationToken cancellationToken);
}

/// <summary>One slot, whatever it currently is - free, held, booked, cancelled, a no-show or a
/// deliberate block. Ordered oldest first by the read store, the plain reading order of a
/// week.</summary>
/// <param name="ServiceId">Null on a <see cref="EventStatus.Blocked"/> row - a closure is not a
/// service (<see cref="Event.ServiceId"/>'s own remarks).</param>
/// <param name="ServiceName">Resolved alongside <paramref name="ServiceId"/>, never permission-gated:
/// a service name is the shop's own catalogue, not personal data.</param>
/// <param name="CustomerId">Not personal data itself - a foreign key, not a phone number or a name -
/// so it is never gated, exactly like <see cref="PendingBookingRow.CustomerId"/>. Carried specifically
/// so a caller can tell "nobody holds this slot" (null) apart from "somebody does, and I may not see
/// who" (non-null <see cref="CustomerId"/> with a null <see cref="CustomerDisplayName"/>) - a
/// distinction <see cref="IPendingBookingReadStore"/> never needed, because every row in that queue
/// already has a customer.</param>
/// <param name="CustomerDisplayName">Null for either of two reasons the row alone does not
/// distinguish - see <paramref name="CustomerId"/> for how a caller tells them apart.</param>
/// <param name="Phone">The same two-reasons-for-null story as <paramref name="CustomerDisplayName"/>,
/// and see <see cref="PendingBookingRow.Phone"/> for why "permitted but nothing on file" is not a
/// third state anything here can produce.</param>
public readonly record struct WorkerSlotRow(
    EventId EventId,
    DateOnly LocalDate,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    EventStatus Status,
    ServiceId? ServiceId,
    string? ServiceName,
    CustomerId? CustomerId,
    string? CustomerDisplayName,
    PhoneNumber? Phone);
