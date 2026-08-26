using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The shared pending-bookings queue: everything awaiting an operator's veto, across every one of a
/// tenant's calendars.
///
/// <para><b>One queue, not one per operator, and not one per calendar.</b> The product spec is
/// explicit, and it mirrors AGO Chat's own unassigned-conversation queue: whoever is around handles
/// whatever arrived. There is deliberately no assignment step, no "mine", and no claim - which is
/// also why <c>Ago.Calendar.Domain.Operator</c> carries no presence and no capacity (`20-01` said so
/// at the time, calling a status column with no reader "a guess about `20-04`"). This item is `20-04`
/// and it does not want one.</para>
///
/// <para><b>A read store, not a repository</b> (adr/0004): it returns rows shaped for a screen, never
/// aggregates. Nothing here can be mutated and nothing here is loaded to be saved - the three
/// operator actions go through <c>IEventRepository</c> and the aggregate, and this exists so that
/// drawing a queue of two hundred pending bookings does not materialise two hundred
/// <see cref="Event"/>s with their invariants to display six columns.</para>
///
/// <para><b>Tenant-scoped, never global.</b> A queue keyed on anything narrower than the tenant would
/// contradict the shared-queue design; a queue keyed on anything wider would be a cross-tenant read.
/// The parameter is the whole isolation story here, and <c>events.tenant_id</c> exists on the row
/// (adr/0049's deliberate denormalisation) precisely so this is one indexed predicate rather than a
/// join through <c>calendars</c>.</para>
/// </summary>
public interface IPendingBookingReadStore
{
    /// <summary>
    /// Oldest deadline first - the order an operator should work them in, because the row nearest its
    /// deadline is the one about to be confirmed by default and is therefore the last moment a veto
    /// is still possible.
    /// </summary>
    /// <param name="now">Used only to mark which rows are already past their deadline, never to
    /// filter them out. See <see cref="PendingBookingRow.IsOverdue"/>.</param>
    Task<IReadOnlyList<PendingBookingRow>> GetPendingForTenantAsync(
        TenantId tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// One row of the queue. Ids plus the handful of values a list needs; no navigation, no aggregate.
/// </summary>
/// <param name="EventId">What the three operator actions address.</param>
/// <param name="CalendarId">Which calendar it belongs to - shown, not filtered on, because the queue
/// spans all of them and an operator still needs to know which shop floor they are looking at.</param>
/// <param name="WorkerId">Who the visit is with.</param>
/// <param name="ServiceId">What was booked.</param>
/// <param name="CustomerId">Resolves the lead card. The queue carries no name and no phone number -
/// a list does not need them, and a read model that joined them would put personal data into every
/// row of a screen an operator leaves open all day.</param>
/// <param name="StartsAt">When the visit is.</param>
/// <param name="EndsAt">Exclusive.</param>
/// <param name="LocalDate">The business-local day, as the shop names it (adr/0049).</param>
/// <param name="ConfirmationDeadline">When it auto-confirms if nobody acts.</param>
/// <param name="IsOverdue">
/// <b>The sweep's health, visible on the one screen a human already looks at.</b> True when the
/// deadline has passed and the row is still pending - which should never last more than a tick, so a
/// row that shows it means the sweep is not doing its job. Included on the row rather than filtered
/// out for exactly that reason: hiding overdue rows would make a broken sweep invisible to the only
/// person in a position to notice, while the customer has already been told they are booked.
/// </param>
public readonly record struct PendingBookingRow(
    EventId EventId,
    CalendarId CalendarId,
    WorkerId WorkerId,
    ServiceId ServiceId,
    CustomerId CustomerId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    DateTimeOffset ConfirmationDeadline,
    bool IsOverdue);
