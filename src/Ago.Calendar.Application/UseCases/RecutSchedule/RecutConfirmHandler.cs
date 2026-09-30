using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>
/// `20-16`: applies an operator's per-booking decisions and moves <see cref="WorkerSchedule.MaterializeFrom"/>
/// back to <see cref="RecutConfirm.From"/> - the one place in this product allowed to call
/// <see cref="WorkerSchedule.RecutFrom"/>, and ADR-0085's own subject.
///
/// <para><b>The staleness check runs once, up front, against every day in range - before this method
/// writes anything.</b> It re-reads <see cref="IEventRepository.ListForDayAsync"/> for the whole
/// window, recomputes <see cref="RecutFingerprint"/> over the current booking set, and refuses the
/// entire request if it disagrees with <see cref="RecutConfirm.Fingerprint"/>. This is what the item's
/// own done-when demands: a booking created between preview and confirm must refuse the whole
/// operation, not apply the operator's decisions to a world that no longer exists. See
/// <see cref="RecutErrors.DayChangedConcurrently"/> for the narrower, already-past-this-check residual
/// this handler does still accept, and why.</para>
///
/// <para><b>Per day: 0..N real cancellations through <see cref="CancelBookingHandler"/>, then one
/// <see cref="IEventRepository.ReplaceDayAsync"/> - two atomic writes, not one enclosing transaction.</b>
/// The item is explicit that cancellation must go through the ordinary cancellation use case, "never a
/// raw delete", so the row survives as <see cref="EventStatus.Cancelled"/> - not deleted, exactly like
/// any other operator cancellation in this product, and no more and no less: <c>events</c> carries no
/// persisted reason column today (<see cref="CancellationReason"/> lives only on the transient
/// <see cref="EventCancelled"/> domain event <see cref="CancelBookingHandler"/> clears before saving,
/// the same as <c>RejectBookingHandler</c>'s own identical cancellation already did before this item -
/// a pre-existing product characteristic this item inherits rather than one it introduces or is
/// scoped to fix). <see cref="CancelBookingHandler"/> owns and commits its own unit of work
/// (<see cref="IEventRepository.SaveAsync"/>), and <c>IEventRepository</c> declares no method that
/// spans a set of individual saves and a day-wide replace in one statement - inventing one for this,
/// this item's only caller, would be exactly the kind of speculative port shape
/// <see cref="IEventRepository"/>'s own remarks on the missing <c>TryClaimAsync</c> already refused to
/// guess. What this buys instead: if <see cref="IEventRepository.ReplaceDayAsync"/> throws
/// <see cref="SlotOverlapException"/> for a day (a claim landed on it in the tiny window after the
/// staleness check above), the cancellations already committed for that day and every earlier day in
/// this request stand - a real, executed decision, not a draft - while the cursor itself is not
/// advanced at all (see below), so a retried confirm only has the remaining days left to do. This is
/// the identical residual <c>EditDayBoundaryHandler</c>'s own remarks already accept between its
/// pre-read and its write, extended from one day to a request that spans many.</para>
///
/// <para><b>The cursor moves only after every day in range has been decided (cleared-and-recut, or
/// left in the old grid) with no failure.</b> Moving it earlier would record "this range has been
/// re-cut" against a request that stopped partway through - the cursor is supposed to mean the ordinary
/// forward job can trust everything from here on is either freshly cut or deliberately preserved, and
/// that is only true once the whole loop below has finished.</para>
///
/// <para><b>`26-315`: a worker with zero materialised slots bootstraps instead of running any of the
/// above.</b> Every check and every write this handler otherwise performs - the staleness fingerprint,
/// the per-booking decisions, <see cref="WorkerSchedule.RecutFrom"/> itself - assumes a cursor that has
/// already been cut past, which is never true for a schedule sitting at its own untouched initial
/// cursor (the state a freshly saved schedule is in until the daily job, or this handler, first runs
/// for it). Detected the identical way <see cref="RecutPreviewHandler"/> detects it - zero
/// <see cref="IEventRepository.ListMaterializedLocalDatesAsync"/> results anywhere in
/// <c>[today, today + HorizonDays]</c> - and handled by delegating to
/// <see cref="MaterializeAvailabilityHandler"/>, the identical handler the daily job itself calls, so
/// the very first cut and every later one are produced by the exact same code rather than a second,
/// parallel implementation of <c>DayGenerator</c>'s own cutting logic. Re-checked fresh at confirm time
/// rather than trusted from the preview: if the daily job (or another operator) materialised something
/// in the gap between preview and confirm, this condition is simply no longer true and the ordinary
/// regression path below runs instead - the same self-healing role the fingerprint check plays for the
/// non-bootstrap path, without needing a second staleness mechanism invented for this one.</para>
///
/// <para><b>Scoped to the calendar, not to this one worker - unlike every other branch of this
/// handler.</b> <c>MaterializeAvailability</c> only exists at calendar granularity (the daily
/// job's own unit of work); there is no per-worker overload to call instead, and adding one just for
/// this branch would be exactly the new, speculative port surface this item's own scope refuses to
/// invent for a contained fix. In practice this means a bootstrap triggered for one worker also
/// materialises any calendar sibling that independently happens to need it too (this product already
/// allows more than one worker per calendar, per this class's own existing tests) - a harmless,
/// idempotent side effect in the same direction as the fix itself, never a destructive one.</para>
/// </summary>
public sealed class RecutConfirmHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkerScheduleRepository schedules,
    IWorkingHoursRuleRepository rules,
    IEventRepository events,
    IWallClockResolver wallClock,
    IIdGenerator idGenerator,
    IPermissionChecker permissions,
    IClock clock,
    CancelBookingHandler cancelBooking,
    MaterializeAvailabilityHandler materialize)
{
    public async Task<Result<RecutConfirmResult>> HandleAsync(RecutConfirm command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return RecutErrors.Forbidden(Permission.CalendarConfigure);
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return RecutErrors.WorkerNotFound(command.WorkerId);
        }

        if (worker.Calendars.Count == 0)
        {
            return RecutErrors.WorkerNotOnACalendar(command.WorkerId);
        }

        var calendar = await calendars.GetByIdAsync(worker.Calendars[0].CalendarId, cancellationToken);
        if (calendar is null)
        {
            return RecutErrors.WorkerNotOnACalendar(command.WorkerId);
        }

        var schedule = await schedules.GetByWorkerIdAsync(command.WorkerId, cancellationToken);
        if (schedule is null)
        {
            return RecutErrors.WorkerHasNoSchedule(command.WorkerId);
        }

        var now = clock.UtcNow;
        var today = wallClock.ToLocalDate(calendar.TimeZone, now);
        var lastDay = today.AddDays(schedule.HorizonDays);

        // `26-315`: the bootstrap branch, ahead of every regression check below - see this class's own
        // remarks for why a never-materialised schedule cannot satisfy them at all, and why re-checking
        // this fresh (rather than trusting the preview) is itself the concurrency guard this branch
        // needs. `command.From`, `command.Fingerprint` and `command.Decisions` are all irrelevant to a
        // worker with nothing materialised yet, so none of them are read below this point.
        var materializedDays = await events.ListMaterializedLocalDatesAsync(
            calendar.Id, worker.Id, today, lastDay, cancellationToken);
        if (materializedDays.Count == 0)
        {
            // The same `firstDay` `MaterializeAvailabilityHandler` itself computes - captured *before*
            // that handler runs, deliberately: `schedule` is the same tracked EF entity
            // `MaterializeAvailabilityHandler` loads and calls `AdvanceCursor` on (both resolve
            // `IWorkerScheduleRepository` against the one scoped `DbContext` this request shares), so
            // reading `schedule.MaterializeFrom` after the call below would already see the *new*,
            // past-the-horizon cursor rather than the one this bootstrap actually started from.
            var firstDay = today > schedule.MaterializeFrom ? today : schedule.MaterializeFrom;

            // Fully qualified: this file's own namespace (`...UseCases.RecutSchedule`) nests directly
            // under `...UseCases`, the same parent `...UseCases.MaterializeAvailability` nests under -
            // so the bare type name `MaterializeAvailability` resolves to that sibling *namespace*
            // instead of the record inside it (CS0118), the identical trap `AvailabilityMaterializationJob`'s
            // own remarks already document for `Worker`/`Ago.Calendar.Worker`.
            var materializeCommand =
                new Ago.Calendar.Application.UseCases.MaterializeAvailability.MaterializeAvailability(calendar.Id);
            var result = await materialize.HandleAsync(materializeCommand, cancellationToken);

            // Reported here only so the caller sees an honest day range for what just happened, never
            // re-derived to decide anything: the handler above already did the actual cutting and
            // cursor advance.
            var bootstrappedDays = new List<DateOnly>();
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                bootstrappedDays.Add(day);
            }

            return new RecutConfirmResult(bootstrappedDays, [], 0, result.SlotsInserted, 0, Bootstrapped: true);
        }

        if (command.From < today)
        {
            return RecutErrors.FromBeforeToday(command.From, today);
        }

        if (command.From >= schedule.MaterializeFrom)
        {
            return RecutErrors.NotARegression(command.From, schedule.MaterializeFrom);
        }

        if (lastDay < command.From)
        {
            return RecutErrors.HorizonBeforeFrom(command.From, lastDay);
        }

        // One read of every day in range, up front - the input to both the staleness check and the
        // write loop below, so the write loop never re-derives what "in range" means a second time
        // from a second read that could itself have moved.
        var dayRows = new List<(DateOnly Day, IReadOnlyList<Event> Rows)>();
        var fingerprintInput = new List<(Guid, EventStatus)>();
        for (var day = command.From; day <= lastDay; day = day.AddDays(1))
        {
            var rows = await events.ListForDayAsync(calendar.Id, worker.Id, day, cancellationToken);
            dayRows.Add((day, rows));
            fingerprintInput.AddRange(
                rows.Where(row => HoldsACustomer(row.Status)).Select(row => (row.Id.Value, row.Status)));
        }

        var currentFingerprint = RecutFingerprint.Compute(fingerprintInput);
        if (!string.Equals(currentFingerprint, command.Fingerprint, StringComparison.Ordinal))
        {
            return RecutErrors.Stale();
        }

        // Every PendingConfirmation/Booked row in range needs an explicit decision. A NoShow row
        // needs none - see RecutBookingPreview.CanDecide - it always forces its day to be skipped.
        var decisionsByBooking = command.Decisions.ToDictionary(decision => decision.BookingId);
        foreach (var (_, rows) in dayRows)
        {
            foreach (var row in rows.Where(row => IsDecidable(row.Status)))
            {
                if (!decisionsByBooking.ContainsKey(row.Id))
                {
                    return RecutErrors.MissingDecision(row.Id);
                }
            }
        }

        var workerRules = schedule.Kind == ScheduleKind.Weekly
            ? (await rules.ListForCalendarAsync(calendar.Id, cancellationToken))
                .Where(rule => rule.WorkerId == worker.Id)
                .ToList()
            : [];

        var recutDays = new List<DateOnly>();
        var skippedDays = new List<DateOnly>();
        var slotsDeleted = 0;
        var slotsInserted = 0;
        var bookingsCancelled = 0;

        foreach (var (day, rows) in dayRows)
        {
            var bookings = rows.Where(row => HoldsACustomer(row.Status)).ToList();

            // A NoShow row cannot be decided (Event.Cancel refuses it) - it is always a keeper. A
            // PendingConfirmation/Booked row is a keeper exactly when the operator said Keep.
            var kept = bookings.Any(row =>
                row.Status == EventStatus.NoShow || decisionsByBooking[row.Id].Decision == RecutDecision.Keep);

            if (kept)
            {
                // THE decided behaviour: any kept booking leaves the whole day untouched in the old
                // grid - adr/0049's exclusion constraint is real physics here, not a preference; a
                // partial re-cut around a kept booking is out of scope by construction, not by choice.
                skippedDays.Add(day);
                continue;
            }

            // Every PendingConfirmation/Booked row on this day, if any, was decided Cancel (the only
            // way `kept` could be false with bookings present). Cancel each for real, through the
            // ordinary use case, before touching the grid.
            foreach (var booking in bookings)
            {
                var cancelResult = await cancelBooking.HandleAsync(
                    new CancelBooking(command.OperatorId, command.TenantId, booking.Id), cancellationToken);
                if (cancelResult.IsFailure)
                {
                    // Cancellation itself refused (e.g. the booking moved on again between the read
                    // above and this call) - stop here rather than press on with a day whose
                    // cancellation half did not complete. Days finished before this one stand; see the
                    // handler's own remarks.
                    return cancelResult.Error!.Value;
                }

                bookingsCancelled++;
            }

            var replacements = DayGenerator
                .GenerateDay(calendar, worker, schedule, day, workerRules, wallClock, idGenerator, now)
                .ToList();

            var deletable = rows.Count(row => row.Status is EventStatus.Available or EventStatus.Blocked);

            try
            {
                await events.ReplaceDayAsync(calendar.Id, worker.Id, day, replacements, cancellationToken);
            }
            catch (SlotOverlapException)
            {
                return RecutErrors.DayChangedConcurrently(day);
            }

            slotsDeleted += deletable;
            slotsInserted += replacements.Count;
            recutDays.Add(day);
        }

        // Only now, with every day in range decided and no failure - see the handler's own remarks on
        // why moving this earlier would misstate what the cursor promises.
        schedule.RecutFrom(command.From, now);
        await schedules.SaveAsync(schedule, cancellationToken);

        return new RecutConfirmResult(recutDays, skippedDays, slotsDeleted, slotsInserted, bookingsCancelled);
    }

    private static bool HoldsACustomer(EventStatus status) =>
        status is EventStatus.PendingConfirmation or EventStatus.Booked or EventStatus.NoShow;

    private static bool IsDecidable(EventStatus status) =>
        status is EventStatus.PendingConfirmation or EventStatus.Booked;
}
