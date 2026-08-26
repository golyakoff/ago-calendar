using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <param name="WorkerIds">Who is on this calendar. Ids rather than nested worker objects: a worker
/// belongs to one calendar in v1, so nesting would duplicate every worker under exactly one calendar
/// and give a reader two places to look.</param>
public readonly record struct ConfiguredCalendar(
    CalendarId CalendarId,
    string Name,
    string TimeZone,
    int BufferMinutes,
    bool IsPublished,
    IReadOnlyList<Guid> WorkerIds,
    IReadOnlyList<ConfiguredWorkingHoursRule> WorkingHours);

public readonly record struct ConfiguredWorker(
    WorkerId WorkerId, string DisplayName, bool IsActive, IReadOnlyList<Guid> ServiceIds);

public readonly record struct ConfiguredService(ServiceId ServiceId, string Name, int DurationMinutes);

public readonly record struct ConfiguredWorkingHoursRule(
    WorkingHoursRuleId RuleId, WorkerId WorkerId, DayOfWeek DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

/// <param name="PublicKey">What the shop pastes into its own page. The console is the only place this
/// is ever shown, and it is why the screen exists at all: without it nobody can write the script tag
/// `20-06`'s Done-when asks a stranger's page to carry.</param>
public readonly record struct TenantConfiguration(
    string TenantName,
    string PublicKey,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<ConfiguredCalendar> Calendars,
    IReadOnlyList<ConfiguredWorker> Workers,
    IReadOnlyList<ConfiguredService> Services);

/// <summary>
/// Everything the setup screen draws, in one call.
///
/// <para><b>Through the repositories rather than a Dapper read model, and that is a line worth
/// drawing explicitly rather than an inconsistency with adr/0004.</b> The read models in this product
/// (<c>PendingBookingReadStore</c>, <c>BookingSurfaceReadStore</c>) exist where the row count is
/// unbounded and the shape is a screen's, not an aggregate's - a queue, a list of free slots. A
/// tenant's configuration is neither: it is a handful of rows, and every one of them is an aggregate
/// the very next request will load and mutate. Writing a second SQL description of the same five
/// tables would buy nothing and would be a second place for the shape to be wrong.</para>
///
/// <para><b>Gated on the same permission the writes are.</b> A read that were open to any
/// authenticated operator would hand out <see cref="TenantConfiguration.PublicKey"/> and the origin
/// list, which is the configuration of the public surface - not a secret, but not a thing to give
/// away either.</para>
/// </summary>
public sealed class GetTenantConfigurationHandler(
    ITenantRepository tenants,
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IServiceRepository services,
    IWorkingHoursRuleRepository rules,
    IPermissionChecker permissions)
{
    public async Task<Result<TenantConfiguration>> HandleAsync(
        GetTenantConfiguration query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var tenant = await tenants.GetByIdAsync(query.TenantId, cancellationToken);
        if (tenant is null)
        {
            return ConfigurationErrors.TenantNotFound(query.TenantId);
        }

        var tenantWorkers = await workers.ListForTenantAsync(tenant.Id, cancellationToken);
        var tenantServices = await services.ListForTenantAsync(tenant.Id, cancellationToken);
        var tenantCalendars = await calendars.ListForTenantAsync(tenant.Id, cancellationToken);

        var configured = new List<ConfiguredCalendar>(tenantCalendars.Count);
        foreach (var calendar in tenantCalendars)
        {
            var calendarRules = await rules.ListForCalendarAsync(calendar.Id, cancellationToken);
            configured.Add(new ConfiguredCalendar(
                calendar.Id,
                calendar.Name,
                calendar.TimeZone.Value,
                calendar.BufferMinutes,
                calendar.IsPublished,
                [.. tenantWorkers.Where(worker => worker.WorksIn(calendar.Id)).Select(worker => worker.Id.Value)],
                [
                    .. calendarRules.Select(rule => new ConfiguredWorkingHoursRule(
                        rule.Id, rule.WorkerId, rule.DayOfWeek, rule.StartsAt, rule.EndsAt)),
                ]));
        }

        return Result<TenantConfiguration>.Success(new TenantConfiguration(
            tenant.Name,
            tenant.PublicKey.Value,
            tenant.AllowedOrigins,
            configured,
            [
                .. tenantWorkers.Select(worker => new ConfiguredWorker(
                    worker.Id,
                    worker.DisplayName,
                    worker.IsActive,
                    [.. worker.Services.Select(offering => offering.ServiceId.Value)])),
            ],
            [
                .. tenantServices.Select(service => new ConfiguredService(
                    service.Id, service.Name, (int)service.Duration.TotalMinutes)),
            ]));
    }
}
