using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.EditDayBoundary;

/// <summary>
/// Shortens or lengthens one already-materialised day by regenerating its slots between new
/// wall-clock boundaries.
///
/// <para><b>Regenerates the whole day rather than nudging its first and last row.</b> Moving only
/// the boundary events is the reading the item's title suggests and it produces a grid that is no
/// longer a grid: a first slot of a different length to its neighbours, and a lengthened day with a
/// gap where the buffer should be. Rebuilding the day from the same <see cref="SlotGrid"/> the
/// materialiser uses means one definition of what a day looks like, so a hand-edited Tuesday and a
/// generated Wednesday are the same shape - which matters most for the thing nobody tests, the
/// buffer.</para>
///
/// <para><b>How the edit survives the next materialisation run.</b> It does not need to defend
/// itself: the run skips every day that has any event row on it, and this day has the rows this
/// handler just wrote. The edit is not marked, flagged or remembered anywhere - the absence of a
/// mechanism is the mechanism, and it is why the invariant is stated as "the job only ever fills
/// empty days" rather than as "the job avoids edited days", which would need a way to know.</para>
///
/// <para><b>What it refuses, and what it is allowed to overwrite.</b> A day holding a
/// <c>PendingConfirmation</c>, <c>Booked</c> or <c>NoShow</c> row is refused outright: a customer is
/// attached to it, and moving the day's shape under them is a cancellation, which has to tell them.
/// <c>Available</c> and <c>Blocked</c> rows are replaced - so this is also the one way to undo a day
/// off, which is deliberate and is the only way v1 offers. <c>Cancelled</c> rows are left alone as
/// history. The list is enforced by the delete's own <c>WHERE</c> clause rather than by the check
/// below; see <see cref="IEventRepository.ReplaceDayAsync"/>.</para>
///
/// <para><b>The residual under a concurrent claim, stated rather than implied.</b> The booking check
/// below reads a snapshot, so a customer can claim a slot after it and before the write. What is
/// guaranteed either way is that the booking survives - it is not deletable, and a replacement
/// landing on top of it is refused by the exclusion constraint. What is *not* guaranteed is the
/// rejection: if the new window happens not to overlap the slot that was just claimed (shortening a
/// day to the afternoon while somebody books the morning), the edit commits and the day ends up with
/// both. That outcome is coherent - the booking is intact and the rest of the day has the shape the
/// tenant asked for - and closing the window would mean locking the whole day for an edit, which is a
/// worse trade for an operator-facing action nobody performs concurrently on purpose.</para>
/// </summary>
public sealed class EditDayBoundaryHandler(
    IBookingCalendarRepository calendars,
    IPermissionChecker permissions,
    IWorkerRepository workers,
    IServiceRepository services,
    IEventRepository events,
    IWallClockResolver wallClock,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(EditDayBoundary command, CancellationToken cancellationToken)
    {
        // `20-06`: the actor check comes before the shape check, so a caller who may not act here
        // learns nothing about whether their times were well formed.
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AvailabilityErrors.Forbidden(Permission.CalendarConfigure);
        }

        if (command.ClosesAt <= command.OpensAt)
        {
            return new Error(
                "availability.invalid_day_boundary",
                $"A day must close after it opens; got {command.OpensAt:HH\\:mm} .. {command.ClosesAt:HH\\:mm}.");
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);

        // The tenant on the calendar, not the tenant on the token - see DeleteDayOffHandler.
        if (calendar is null || calendar.TenantId != command.TenantId)
        {
            return AvailabilityErrors.CalendarNotFound(command.CalendarId);
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || !worker.WorksIn(calendar.Id))
        {
            return AvailabilityErrors.WorkerNotOnCalendar(command.WorkerId, command.CalendarId);
        }

        var day = await events.ListForDayAsync(
            command.CalendarId, command.WorkerId, command.LocalDate, cancellationToken);

        if (day.Count == 0)
        {
            return AvailabilityErrors.DayNotMaterialized(command.LocalDate);
        }

        if (day.Any(HoldsACustomer))
        {
            return AvailabilityErrors.DayHasBookings(command.LocalDate);
        }

        var slotLength = LongestServiceOf(worker, await services.ListForTenantAsync(calendar.TenantId, cancellationToken));
        if (slotLength is null)
        {
            return AvailabilityErrors.WorkerNotOnCalendar(command.WorkerId, command.CalendarId);
        }

        var now = clock.UtcNow;

        // The single conversion, again - the same call the materialiser makes, on a hand-typed
        // window instead of a stored rule. Null means a DST gap left no real time between the two
        // edges the tenant typed, which is a day with no working time in it and therefore the same
        // outcome as a day off: the replacement set is empty and the day is left with nothing
        // bookable.
        var window = wallClock.ToInstantWindow(
            calendar.TimeZone, command.LocalDate, command.OpensAt, command.ClosesAt);

        var replacements = window is null
            ? []
            : SlotGrid.Fill(window.Value, slotLength.Value, TimeSpan.FromMinutes(calendar.BufferMinutes))
                .Where(slot => slot.EndsAt > now)
                .Select(slot => Event.Materialize(
                    new EventId(idGenerator.NewId(now)),
                    calendar.TenantId,
                    calendar.Id,
                    worker.Id,
                    slot,
                    command.LocalDate,
                    now))
                .ToList();

        try
        {
            await events.ReplaceDayAsync(
                command.CalendarId, command.WorkerId, command.LocalDate, replacements, cancellationToken);
        }
        catch (SlotOverlapException)
        {
            return AvailabilityErrors.DayChangedConcurrently(command.LocalDate);
        }

        return Result.Success();
    }

    private static bool HoldsACustomer(Event @event) =>
        @event.Status is EventStatus.PendingConfirmation or EventStatus.Booked or EventStatus.NoShow;

    /// <summary>The same rule the materialiser uses, for the same reason: a regenerated day whose
    /// slots were a different length to the generated ones would make a hand-edited Tuesday
    /// unbookable for the worker's longest service. See
    /// <c>MaterializeAvailabilityHandler.SlotLengthFor</c> for why longest and not shortest.</summary>
    private static TimeSpan? LongestServiceOf(Worker worker, IReadOnlyList<Service> tenantServices)
    {
        TimeSpan? longest = null;
        foreach (var service in tenantServices.Where(service => worker.Offers(service.Id)))
        {
            if (longest is null || service.Duration > longest)
            {
                longest = service.Duration;
            }
        }

        return longest;
    }
}
