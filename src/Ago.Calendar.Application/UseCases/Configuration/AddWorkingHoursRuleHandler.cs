using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// Gives a worker an ordinary weekly window on a calendar - the input `20-02`'s materialiser turns
/// into slots.
///
/// <para><b>Nothing here converts a time zone, and nothing here may.</b> The command carries
/// <see cref="TimeOnly"/> because "we open at nine" is a statement about a clock on a wall
/// (<see cref="WorkingHoursRule"/>); the single conversion to instants happens in the materialiser,
/// through the calendar's own <see cref="CalendarTimeZone"/>. A handler that resolved the window here
/// would freeze one offset into a rule that has to survive two DST transitions a year.</para>
/// </summary>
public sealed class AddWorkingHoursRuleHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkingHoursRuleRepository rules,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<WorkingHoursRuleId>> HandleAsync(
        AddWorkingHoursRule command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        if (calendar is null || calendar.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("calendar", command.CalendarId.Value);
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("worker", command.WorkerId.Value);
        }

        WorkingHoursRule rule;
        try
        {
            // Takes both aggregates, not their ids: this factory is the only place the
            // one-calendar-per-worker limit is checkable, because only the worker knows which
            // calendar it belongs to.
            rule = WorkingHoursRule.For(
                new WorkingHoursRuleId(idGenerator.NewId(clock.UtcNow)),
                worker,
                calendar,
                command.DayOfWeek,
                command.StartsAt,
                command.EndsAt);
        }
        catch (Exception exception)
            when (exception is ArgumentException or ArgumentOutOfRangeException
                or TenantMismatchException or WorkerCalendarLimitException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        await rules.AddAsync(rule, cancellationToken);
        return Result<WorkingHoursRuleId>.Success(rule.Id);
    }
}
