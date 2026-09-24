using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `26-97`: <c>DELETE /working-hours/{ruleId}</c> - "this is not a working day after all".
///
/// <para><b>The same tenant boundary as <see cref="UpdateWorkingHoursRuleHandler"/>, resolved the
/// same way</b> - through the rule's own calendar, reported as
/// <see cref="ConfigurationErrors.NotFound"/> so a cross-tenant id is never confirmed to exist. The
/// worker is loaded for the same second, aggregate-level check the edit path makes; a rule whose
/// worker has drifted out of this tenant is not one this handler will act on even though the delete
/// itself would technically succeed.</para>
///
/// <para><b>Never refused on booking history, unlike <see cref="DeleteWorkerHandler"/>, and the
/// asymmetry is real rather than an oversight.</b> Deleting a worker orphans the rows that name him;
/// deleting a working-hours rule orphans nothing at all, because the rule is only ever the
/// materialiser's *input*. Slots already cut from it are ordinary <see cref="Event"/> rows that no
/// longer reference it - <c>MaterializeAvailabilityHandler</c> never deletes, and nothing else reads
/// a rule after the fact - so a booked slot survives this call untouched and unchanged. What the
/// operator gets instead of a refusal is <see cref="WorkingHoursReconciliation"/>: the days already
/// cut from these hours, how many live bookings are on them, and the date to re-cut from. See
/// <see cref="WorkingHoursReconciler"/> for the full decision.</para>
/// </summary>
public sealed class DeleteWorkingHoursRuleHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkingHoursRuleRepository rules,
    IPermissionChecker permissions,
    WorkingHoursReconciler reconciler)
{
    public async Task<Result<WorkingHoursReconciliation>> HandleAsync(
        DeleteWorkingHoursRule command, CancellationToken cancellationToken)
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

        // Computed before the delete, not after: the reconciliation describes the days this rule has
        // already cut, and once the row is gone its weekday is no longer readable from anywhere.
        var reconciliation = await reconciler.ComputeAsync(
            command.TenantId, calendar, rule.WorkerId, [rule.DayOfWeek], cancellationToken);

        await rules.DeleteAsync(rule, cancellationToken);

        return reconciliation;
    }
}
