using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `26-97`: <c>PUT /working-hours/{ruleId}</c> - the correction that did not exist until this item.
///
/// <para><b>Three loads before the write, and the middle one is the tenant boundary.</b> A
/// <see cref="WorkingHoursRule"/> carries no <see cref="TenantId"/> of its own - it is scoped to a
/// (worker, calendar) pair - so the tenant is resolved through the rule's own calendar, and a rule
/// whose calendar belongs to somebody else is reported as <see cref="ConfigurationErrors.NotFound"/>,
/// never as "forbidden". That wording is not politeness: an operator of tenant A learning that an id
/// exists in tenant B is a cross-tenant leak however it is phrased, which is the rule
/// <see cref="ConfigurationErrors"/>'s own remarks already state for every other id-addressed write
/// here.</para>
///
/// <para><b>The second tenant check is the domain's, and it is not redundant.</b>
/// <see cref="WorkingHoursRule.ChangeTo"/> re-runs <see cref="TenantMismatchException"/> and the
/// one-calendar-per-worker rule against the freshly loaded aggregates, exactly as
/// <see cref="WorkingHoursRule.For"/> does on the way in. The handler check above answers "may this
/// caller touch this row"; the aggregate's answers "is the row itself still coherent" - a worker
/// moved between tenants by some future path would pass the first and fail the second, and the
/// invariant belongs to the aggregate rather than to whichever handler remembered it.</para>
///
/// <para><b>Always allowed, never silent</b> - see <see cref="WorkingHoursReconciler"/> for the
/// design decision and the reasoning that rejected the alternative of refusing while live bookings
/// exist. The reconciliation is computed <i>after</i> the save and from the rule's own <i>old</i> and
/// <i>new</i> weekday together, because both sets of days are days the operator now has to think
/// about.</para>
/// </summary>
public sealed class UpdateWorkingHoursRuleHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkingHoursRuleRepository rules,
    IPermissionChecker permissions,
    WorkingHoursReconciler reconciler)
{
    public async Task<Result<WorkingHoursRuleUpdated>> HandleAsync(
        UpdateWorkingHoursRule command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var rule = await rules.GetByIdAsync(command.RuleId, cancellationToken);
        if (rule is null)
        {
            return ConfigurationErrors.NotFound("working-hours rule", command.RuleId.Value);
        }

        var calendar = await calendars.GetByIdAsync(rule.CalendarId, cancellationToken);
        if (calendar is null || calendar.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("working-hours rule", command.RuleId.Value);
        }

        var worker = await workers.GetByIdAsync(rule.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("working-hours rule", command.RuleId.Value);
        }

        // Captured before the aggregate moves: an edit that changes the weekday affects the days it
        // is leaving as much as the ones it is arriving on.
        var previousDayOfWeek = rule.DayOfWeek;

        try
        {
            rule.ChangeTo(worker, calendar, command.DayOfWeek, command.StartsAt, command.EndsAt);
        }
        catch (Exception exception)
            when (exception is ArgumentException or ArgumentOutOfRangeException
                or TenantMismatchException or WorkerCalendarLimitException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        await rules.SaveAsync(rule, cancellationToken);

        var reconciliation = await reconciler.ComputeAsync(
            command.TenantId,
            calendar,
            rule.WorkerId,
            previousDayOfWeek == rule.DayOfWeek ? [rule.DayOfWeek] : [previousDayOfWeek, rule.DayOfWeek],
            cancellationToken);

        return new WorkingHoursRuleUpdated(
            rule.Id, rule.WorkerId, rule.CalendarId, rule.DayOfWeek, rule.StartsAt, rule.EndsAt, reconciliation);
    }
}

/// <summary>`26-97`: the corrected rule as it now stands, plus what the correction did not reach.
/// The rule is echoed in full rather than returned as a bare id, so a console that has just submitted
/// a form renders the server's own answer instead of the values it optimistically believes it
/// sent.</summary>
public readonly record struct WorkingHoursRuleUpdated(
    WorkingHoursRuleId RuleId,
    WorkerId WorkerId,
    CalendarId CalendarId,
    DayOfWeek DayOfWeek,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    WorkingHoursReconciliation Reconciliation);
