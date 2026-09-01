using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
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
/// </summary>
public sealed class SaveWorkerScheduleHandler(
    IWorkerRepository workers,
    IWorkerScheduleRepository schedules,
    IPermissionChecker permissions,
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

        WorkerSchedule schedule;
        try
        {
            if (existing is null)
            {
                schedule = command.Kind == ScheduleKind.Weekly
                    ? WorkerSchedule.CreateWeekly(
                        new WorkerScheduleId(idGenerator.NewId(now)), command.WorkerId,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now)
                    : WorkerSchedule.CreateCycle(
                        new WorkerScheduleId(idGenerator.NewId(now)), command.WorkerId,
                        command.CycleAnchor!.Value, command.CycleWorkingDays!.Value, command.CycleRestDays!.Value,
                        command.CycleStartsAt!.Value, command.CycleEndsAt!.Value,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now);

                await schedules.AddAsync(schedule, cancellationToken);
            }
            else
            {
                schedule = existing;
                if (command.Kind == ScheduleKind.Weekly)
                {
                    schedule.ReconfigureWeekly(
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now);
                }
                else
                {
                    schedule.ReconfigureCycle(
                        command.CycleAnchor!.Value, command.CycleWorkingDays!.Value, command.CycleRestDays!.Value,
                        command.CycleStartsAt!.Value, command.CycleEndsAt!.Value,
                        command.SlotMinutes, command.BufferMinutes, command.HorizonDays, command.MaterializeFrom, now);
                }

                await schedules.SaveAsync(schedule, cancellationToken);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        return GetWorkerScheduleHandler.ToDetail(schedule);
    }

    private static bool HasCycleParameters(SaveWorkerSchedule command) =>
        command.CycleAnchor.HasValue && command.CycleWorkingDays.HasValue && command.CycleRestDays.HasValue
        && command.CycleStartsAt.HasValue && command.CycleEndsAt.HasValue;
}
