using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ManualBooking;

/// <summary>
/// `26-268`/`adr/0188`: an operator blocks a slot for a booking that was already taken by phone -
/// «Добавить вручную». Composes exactly the pieces `BookEventHandler` and `RescheduleBookingHandler`
/// already proved: a phone-shaped check, worker/service/schedule resolution, <c>ConsecutiveRunFinder</c>,
/// and a store whose own transaction is the only place anything is decided or written.
///
/// <para><b>Why this is a handler-orchestrated composition, not a new domain method.</b> The identical
/// reasoning <c>RescheduleBookingHandler</c>'s own remarks give: the write spans a raw atomic claim and
/// an EF person upsert and two staged outbox rows, and an aggregate sees only itself - it cannot span
/// the person row and the slot rows, so the composition belongs here and the transaction belongs to
/// <see cref="IManualBookingStore"/> beneath it (adr/0004).</para>
///
/// <para><b>Order of operations, and why each step is where it is.</b> Permission first
/// (<see cref="Permission.BookingCreate"/> alone - `adr/0188`/§2: not paired with
/// <see cref="Permission.CustomerEdit"/>, because the client this handler creates is the booking's own
/// trusted side-effect, the same way a widget booking mints one with no permission check at all) - a
/// caller with no right never learns whether the calendar, worker or slot exist. Then the phone shape
/// (cheap, no I/O, <c>BookEventHandler</c>'s own pattern of turning <see cref="PhoneNumber"/>'s
/// <see cref="ArgumentException"/> into an ordinary rejection). Then calendar/worker/service/schedule
/// resolution and the courtesy run-find - every read this handler trusts nothing from except <i>which
/// ids to ask the claim for</i> (rule 8). The store's own claim is the only step that changes
/// anything.</para>
///
/// <para><b>Always mints a new person (`adr/0188`/§3.4).</b> Phone-based recognition - "is this number
/// already a client" - is deferred to its own slice; this handler mints
/// (<see cref="IIdGenerator.NewId"/>) every time, exactly as <c>BookEventHandler</c> does for the
/// public/operator path with no chat origin.</para>
/// </summary>
public sealed class EnterManualBookingHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IServiceRepository services,
    IWorkerScheduleRepository schedules,
    IEventRepository events,
    IManualBookingStore store,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<BookingConfirmation>> HandleAsync(
        EnterManualBooking command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingCreate, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingCreate);
        }

        PhoneNumber phone;
        try
        {
            phone = new PhoneNumber(command.Phone);
        }
        catch (ArgumentException exception)
        {
            // The one place a domain constructor's exception is turned into an ordinary rejection -
            // BookEventHandler's own precedent. An operator who fat-fingered a number is not a bug.
            return BookingErrors.InvalidPhone(exception.Message);
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        // Wrong-tenant collapses into "not found", the identical cross-tenant guard every other
        // lifecycle handler in this product applies (RescheduleBookingHandler.WrongTenant's own
        // remarks) - an operator of tenant A must not learn that a calendar id belongs to tenant B.
        if (calendar is null || calendar.TenantId != command.TenantId)
        {
            return BookingErrors.CalendarNotFound();
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId || !worker.IsActive
            || !worker.Offers(command.ServiceId))
        {
            // Collapsed into one refusal - BookEventHandler's own reasoning: a worker who does not
            // exist, is inactive, or simply does not offer this service are all, from the operator's
            // side, "you cannot book this pair" - the one real invariant worth stating precisely is
            // which service is at fault, not which of those three reasons produced it.
            return BookingErrors.ServiceNotOffered();
        }

        var service = await services.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null || service.TenantId != command.TenantId || !service.IsActive)
        {
            return BookingErrors.ServiceNotOffered();
        }

        var schedule = await schedules.GetByWorkerIdAsync(worker.Id, cancellationToken);
        if (schedule is null)
        {
            // No grid to walk without a schedule - the identical refusal BookEventHandler gives for
            // the same missing precondition.
            return BookingErrors.ServiceNotOffered();
        }

        var target = await events.GetByIdAsync(command.StartEventId, cancellationToken);
        if (target is null || target.CalendarId != calendar.Id || target.WorkerId != worker.Id)
        {
            // The picked slot does not exist, or belongs to another calendar/worker than the ones the
            // operator named - collapsed into the ordinary "that time will not work" outcome, the same
            // shape BookEventHandler's own slot/calendar mismatch takes.
            return BookingLifecycleErrors.SlotNoLongerAvailable();
        }

        var dayEvents = await events.ListForDayAsync(
            calendar.Id, worker.Id, target.LocalDate, cancellationToken);

        var run = ConsecutiveRunFinder.FindRun(
            dayEvents,
            command.StartEventId,
            (int)service.Duration.TotalMinutes,
            schedule.SlotMinutes,
            schedule.BufferMinutes,
            schedule.BuffersCountTowardServiceDuration);

        if (run is null)
        {
            return BookingLifecycleErrors.SlotNoLongerAvailable();
        }

        var now = clock.UtcNow;

        // `adr/0188`/§3.4: always a fresh person in this slice - phone-based recognition is its own,
        // later slice. The same `IIdGenerator` every other minted id on this product's write paths
        // comes from.
        var personId = idGenerator.NewId(now);

        var lastSlot = dayEvents.Single(slot => slot.Id == run[^1]);
        var displayName = string.IsNullOrWhiteSpace(command.DisplayName) ? null : command.DisplayName.Trim();

        // Both envelopes built here (Application - clean-architecture.md's own placement for the
        // domain-to-contract mapping), from facts this handler already resolved, and handed to the
        // store to stage on the success path only, inside its transaction (rule 4): a minted person or
        // a "confirmed" fact for a claim that never happened must never reach chat or a downstream
        // consumer.
        var personRegistered = PersonRegisteredMapper.ToEnvelope(
            personId, command.TenantId, phone, displayName, now, idGenerator);
        var bookingConfirmed = BookingConfirmedMapper.ToEnvelope(
            eventId: run[0],
            tenantId: command.TenantId,
            calendarId: calendar.Id,
            personId: personId,
            startsAt: target.StartsAt,
            endsAt: lastSlot.EndsAt,
            localDate: target.LocalDate,
            occurredAt: now,
            idGenerator);

        var confirmation = await store.TryEnterAsync(
            new ManualBookingAttempt(
                command.TenantId,
                calendar.Id,
                run,
                command.ServiceId,
                phone,
                personId,
                now,
                personRegistered,
                bookingConfirmed),
            cancellationToken);

        // Null is the ordinary lost race - IManualBookingStore's own contract, the identical posture
        // every claim-shaped store in this product takes. Never logged at Error, never a 500.
        return confirmation is null
            ? BookingLifecycleErrors.SlotNoLongerAvailable()
            : Result<BookingConfirmation>.Success(confirmation.Value);
    }
}
