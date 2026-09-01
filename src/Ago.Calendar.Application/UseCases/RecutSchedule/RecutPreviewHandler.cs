using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>
/// `20-16`: shows an operator exactly what a re-cut back to <see cref="RecutPreview.From"/> would
/// destroy, before it destroys anything - the item's own stated promise that "destruction only ever
/// happens because a human asked for it, by name, having been shown what they would lose."
///
/// <para><b>Read-only, and built entirely on <see cref="IWorkerSlotReadStore"/> rather than
/// <see cref="IEventRepository"/>.</b> Every field this screen needs - the slot count, every booking's
/// time/status/service, and the customer's name and phone gated on <see cref="Permission.CustomerRead"/>
/// - is exactly what `20-15`'s own read store already assembles for the materialised-slot screen one
/// item over. Querying <see cref="Event"/> aggregates here would mean re-deriving a service name and a
/// gated customer projection a second time for a screen that only ever reads; adr/0004's read/write
/// split is precisely for this - a screen's shape is a read model's job, not a reason to load
/// aggregates whose own state-machine invariants nothing here needs.</para>
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/>, with <see cref="Permission.CustomerRead"/>
/// layered on top for the contact columns only</b> - the identical two-layer shape
/// <c>GetWorkerSlotsHandler</c> already established for the same read store, and `20-12`'s own
/// precedent before that.</para>
/// </summary>
public sealed class RecutPreviewHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkerScheduleRepository schedules,
    IWorkerSlotReadStore slots,
    IWallClockResolver wallClock,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result<RecutPreviewResult>> HandleAsync(RecutPreview query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return RecutErrors.Forbidden(Permission.CalendarConfigure);
        }

        var worker = await workers.GetByIdAsync(query.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != query.TenantId)
        {
            return RecutErrors.WorkerNotFound(query.WorkerId);
        }

        if (worker.Calendars.Count == 0)
        {
            return RecutErrors.WorkerNotOnACalendar(query.WorkerId);
        }

        var schedule = await schedules.GetByWorkerIdAsync(query.WorkerId, cancellationToken);
        if (schedule is null)
        {
            return RecutErrors.WorkerHasNoSchedule(query.WorkerId);
        }

        // v1: exactly one calendar per worker (Worker.JoinCalendar) - there is no second membership
        // to choose between.
        var calendar = await calendars.GetByIdAsync(worker.Calendars[0].CalendarId, cancellationToken);
        if (calendar is null)
        {
            return RecutErrors.WorkerNotOnACalendar(query.WorkerId);
        }

        var today = wallClock.ToLocalDate(calendar.TimeZone, clock.UtcNow);

        if (query.From < today)
        {
            return RecutErrors.FromBeforeToday(query.From, today);
        }

        if (query.From >= schedule.MaterializeFrom)
        {
            return RecutErrors.NotARegression(query.From, schedule.MaterializeFrom);
        }

        var lastDay = today.AddDays(schedule.HorizonDays);
        if (lastDay < query.From)
        {
            return RecutErrors.HorizonBeforeFrom(query.From, lastDay);
        }

        // `20-12`: a *second*, independent permission check - never a reason to refuse the whole
        // preview, only whether the per-booking contact fields are populated. Re-resolved against
        // this caller's real, current roles, the same reasoning `GetWorkerSlotsHandler` gives its own
        // identical second check.
        var canReadContacts = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);

        var rows = await slots.GetForWorkerAsync(
            query.TenantId, query.WorkerId, query.From, lastDay, canReadContacts, cancellationToken);

        var rowsByDay = rows.ToLookup(row => row.LocalDate);

        var days = new List<RecutDayPreview>();
        var fingerprintInput = new List<(Guid, EventStatus)>();

        for (var day = query.From; day <= lastDay; day = day.AddDays(1))
        {
            var dayRows = rowsByDay[day];
            var availableCount = dayRows.Count(row => row.Status == EventStatus.Available);

            var bookings = new List<RecutBookingPreview>();
            foreach (var row in dayRows.Where(row => HoldsACustomer(row.Status)))
            {
                bookings.Add(new RecutBookingPreview(
                    row.EventId,
                    row.StartsAt,
                    row.EndsAt,
                    row.Status,
                    row.ServiceId,
                    row.ServiceName,
                    row.CustomerId,
                    row.CustomerDisplayName,
                    row.Phone,
                    CanDecide: row.Status != EventStatus.NoShow));

                fingerprintInput.Add((row.EventId.Value, row.Status));
            }

            days.Add(new RecutDayPreview(day, availableCount, bookings));
        }

        return new RecutPreviewResult(days, RecutFingerprint.Compute(fingerprintInput));
    }

    /// <summary>The same three statuses <c>CancelBookingHandler</c>'s own remarks and
    /// <c>DeleteDayOffHandler.HoldsACustomer</c> use - a customer is attached to the row, whether or
    /// not the visit has already happened.</summary>
    private static bool HoldsACustomer(EventStatus status) =>
        status is EventStatus.PendingConfirmation or EventStatus.Booked or EventStatus.NoShow;
}
