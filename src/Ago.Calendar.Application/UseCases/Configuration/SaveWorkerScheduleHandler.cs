using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `20-14`: <c>PUT /workers/{id}/schedule</c>. Creates the worker's schedule if none exists yet, or
/// reconfigures the one that does - the same upsert shape <see cref="SaveWorkerSchedule"/>'s own
/// remarks explain.
///
/// <para><b>Every bound - the 180-day horizon cap, the buffer cap, a cycle's own hour ordering, the
/// cursor's forward-only rule - is enforced inside <see cref="WorkerSchedule"/> itself, not here.</b>
/// This handler's own job is only to load the right aggregate and call the right method on it,
/// exactly the shape <see cref="UpdateCalendarHandler"/> and <see cref="CreateWorkerHandler"/> already
/// use: a domain constructor or method says no, and this handler turns that <c>ArgumentException</c>
/// into an ordinary <see cref="Result"/> rather than letting it reach the endpoint as a 500. The
/// reason this matters more here than usual is CLAUDE.md's own instruction to reject the horizon cap
/// in the handler "so a direct API call can't bypass a console-only check" - which this satisfies by
/// construction, since there is no path from this handler to a saved row that does not go through
/// <see cref="WorkerSchedule"/>'s own validation.</para>
///
/// <para><b>`26-315`: stages <see cref="Ago.Calendar.Contracts.WorkerScheduleSaved"/> on every
/// successful save, so <c>Ago.Calendar.Worker</c> materialises this calendar within seconds rather than
/// waiting for the next daily tick.</b> Staged through the ordinary outbox (<see cref="IOutboxWriter"/>),
/// never run inline here - this handler still never touches <c>Event</c> rows or
/// <c>MaterializeAvailabilityHandler</c> itself, keeping the cut off the request path exactly as every
/// other write in this product keeps its own side effects off it (CLAUDE.md rule 4). Skipped entirely
/// for a worker joined to no calendar yet (<see cref="Domain.Worker.Calendars"/> empty) - there is
/// nothing for the consumer to materialise into, the identical guard every other schedule-adjacent read
/// in this product (<c>RecutPreviewHandler.WorkerNotOnACalendar</c>) already applies.</para>
/// </summary>
public sealed class SaveWorkerScheduleHandler(
    IWorkerRepository workers,
    IWorkerScheduleRepository schedules,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<WorkerScheduleDetail>> HandleAsync(
        SaveWorkerSchedule command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("worker", command.WorkerId.Value);
        }

        if (command.Kind == ScheduleKind.Cycle && !HasCycleParameters(command))
        {
            return ConfigurationErrors.Invalid(
                "A cycle schedule needs an anchor date, working days, rest days, and start/end hours.");
        }

        var now = clock.UtcNow;
        var existing = await schedules.GetByWorkerIdAsync(command.WorkerId, cancellationToken);
        var isNew = existing is null;

        WorkerSchedule schedule;
        try
        {
            if (existing is null)
            {
                schedule = command.Kind == ScheduleKind.Weekly
                    ? WorkerSchedule.CreateWeekly(
                        new WorkerScheduleId(idGenerator.NewId(now)), command.WorkerId,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now,
                        command.BuffersCountTowardServiceDuration)
                    : WorkerSchedule.CreateCycle(
                        new WorkerScheduleId(idGenerator.NewId(now)), command.WorkerId,
                        command.CycleAnchor!.Value, command.CycleWorkingDays!.Value, command.CycleRestDays!.Value,
                        command.CycleStartsAt!.Value, command.CycleEndsAt!.Value,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now,
                        command.BuffersCountTowardServiceDuration);
            }
            else
            {
                schedule = existing;
                if (command.Kind == ScheduleKind.Weekly)
                {
                    schedule.ReconfigureWeekly(
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now,
                        command.BuffersCountTowardServiceDuration);
                }
                else
                {
                    schedule.ReconfigureCycle(
                        command.CycleAnchor!.Value, command.CycleWorkingDays!.Value, command.CycleRestDays!.Value,
                        command.CycleStartsAt!.Value, command.CycleEndsAt!.Value,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now,
                        command.BuffersCountTowardServiceDuration);
                }
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        // `26-315`: staged before the schedule row itself commits, onto the identical DbContext
        // AddAsync/SaveAsync below saves - the same "stage, then let the ordinary save commit both
        // together" shape ConfirmBookingHandler's own outbox call uses, which is what CLAUDE.md rule 4
        // actually requires: the state change and its integration event in one transaction, never two.
        // Skipped for a worker on no calendar yet - see this class's own remarks.
        if (worker.Calendars.Count > 0)
        {
            outbox.Enqueue(WorkerScheduleSavedMapper.ToEnvelope(
                worker.Calendars[0].CalendarId, command.WorkerId, now, idGenerator));
        }

        if (isNew)
        {
            await schedules.AddAsync(schedule, cancellationToken);
        }
        else
        {
            await schedules.SaveAsync(schedule, cancellationToken);
        }

        return GetWorkerScheduleHandler.ToDetail(schedule);
    }

    private static bool HasCycleParameters(SaveWorkerSchedule command) =>
        command.CycleAnchor.HasValue && command.CycleWorkingDays.HasValue && command.CycleRestDays.HasValue
        && command.CycleStartsAt.HasValue && command.CycleEndsAt.HasValue;
}
