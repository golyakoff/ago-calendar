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
///
/// <para><b>`26-315`: one deliberate exception to "built entirely on <see cref="IWorkerSlotReadStore"/>",
/// stated above.</b> This handler also takes <see cref="IEventRepository"/>, write-side though it is,
/// for exactly one call: <see cref="IEventRepository.ListMaterializedLocalDatesAsync"/>, to answer "has
/// this worker ever been materialised at all" before the regression checks below run. That question has
/// to be asked before <see cref="RecutPreview.From"/> is validated against
/// <see cref="WorkerSchedule.MaterializeFrom"/>, because a schedule that has never been cut sits at its
/// own initial cursor - never past anything - so every one of those checks refuses every <c>From</c> an
/// operator could send, which is this item's own root cause: a correctly configured worker for which
/// «Пересчёт» has no reachable input at all, not merely one that re-cuts zero rows. This call returns a
/// set of dates, never an <see cref="Event"/> aggregate, so it does not reintroduce the aggregate
/// loading this handler's own read-only design exists to avoid.</para>
/// </summary>
public sealed class RecutPreviewHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkerScheduleRepository schedules,
    IWorkerSlotReadStore slots,
    IEventRepository events,
    IWallClockResolver wallClock,
    IPermissionChecker permissions,
    IContactVisibilityProjectionStore visibility,
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
        var lastDay = today.AddDays(schedule.HorizonDays);

        // `26-315`: the bootstrap check, ahead of every regression check below - see this class's own
        // remarks for why a never-materialised schedule cannot satisfy them at all. Zero materialised
        // days anywhere in the worker's own bookable window means there is nothing yet to preview or
        // destroy; the caller's own `From` does not matter in that state, so it is deliberately never
        // read below this point.
        var materializedDays = await events.ListMaterializedLocalDatesAsync(
            calendar.Id, worker.Id, today, lastDay, cancellationToken);
        if (materializedDays.Count == 0)
        {
            return new RecutPreviewResult([], RecutFingerprint.Compute([]), IsBootstrap: true);
        }

        if (query.From < today)
        {
            return RecutErrors.FromBeforeToday(query.From, today);
        }

        if (query.From >= schedule.MaterializeFrom)
        {
            return RecutErrors.NotARegression(query.From, schedule.MaterializeFrom);
        }

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

        // `23-12`: the identical third, independent read `GetTenantContactsHandler`'s own remarks
        // give for itself, only asked when there is a phone column for it to act on at all.
        var mask = false;
        if (canReadContacts)
        {
            var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
            mask = rung == ContactVisibility.MaskedWithReveal;
        }

        var rows = await slots.GetForWorkerAsync(
            query.TenantId, query.WorkerId, query.From, lastDay, canReadContacts, mask, cancellationToken);

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
                    row.PersonId,
                    row.Phone,
                    row.Masked,
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
