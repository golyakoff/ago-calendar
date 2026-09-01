using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>`20-14`: everything the schedule section of `20-13`'s worker card needs to prefill an
/// edit form. One shape, whichever kind is active - the cycle fields are simply <see langword="null"/>
/// while <see cref="Kind"/> is <see cref="ScheduleKind.Weekly"/>, and vice versa, so the console never
/// has to guess which fields a given kind populates.</summary>
public readonly record struct WorkerScheduleDetail(
    WorkerScheduleId ScheduleId,
    WorkerId WorkerId,
    ScheduleKind Kind,
    DateOnly? CycleAnchor,
    int? CycleWorkingDays,
    int? CycleRestDays,
    TimeOnly? CycleStartsAt,
    TimeOnly? CycleEndsAt,
    int SlotMinutes,
    int BufferMinutes,
    int HorizonDays,
    DateOnly MaterializeFrom,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>`20-14`: <c>GET /workers/{id}/schedule</c>. Gated on the same
/// <see cref="Permission.CalendarConfigure"/> every other configuration screen is.</summary>
public sealed class GetWorkerScheduleHandler(
    IWorkerRepository workers, IWorkerScheduleRepository schedules, IPermissionChecker permissions)
{
    public async Task<Result<WorkerScheduleDetail>> HandleAsync(
        GetWorkerSchedule query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var worker = await workers.GetByIdAsync(query.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != query.TenantId)
        {
            return ConfigurationErrors.NotFound("worker", query.WorkerId.Value);
        }

        var schedule = await schedules.GetByWorkerIdAsync(query.WorkerId, cancellationToken);
        if (schedule is null)
        {
            return ConfigurationErrors.NoSchedule(query.WorkerId);
        }

        return ToDetail(schedule);
    }

    internal static WorkerScheduleDetail ToDetail(WorkerSchedule schedule) => new(
        schedule.Id,
        schedule.WorkerId,
        schedule.Kind,
        schedule.CycleAnchor,
        schedule.CycleWorkingDays,
        schedule.CycleRestDays,
        schedule.CycleStartsAt,
        schedule.CycleEndsAt,
        schedule.SlotMinutes,
        schedule.BufferMinutes,
        schedule.HorizonDays,
        schedule.MaterializeFrom,
        schedule.CreatedAt,
        schedule.UpdatedAt);
}
