using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// A tenant adds a booking calendar.
///
/// <para><b>The tenant is read, not assumed.</b> The permission check already proved this operator
/// holds <see cref="Permission.CalendarConfigure"/> in the tenant they named - but a permission can
/// only be held against a role row, and a role row can outlive nothing. Loading the tenant is what
/// turns "you may configure tenant X" into "tenant X exists", and it is one indexed read on a path
/// that runs once per calendar a shop ever creates.</para>
/// </summary>
public sealed class CreateCalendarHandler(
    ITenantRepository tenants,
    IBookingCalendarRepository calendars,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<CalendarId>> HandleAsync(CreateCalendar command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var tenant = await tenants.GetByIdAsync(command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return ConfigurationErrors.TenantNotFound(command.TenantId);
        }

        var now = clock.UtcNow;

        BookingCalendar calendar;
        try
        {
            calendar = BookingCalendar.Create(
                new CalendarId(idGenerator.NewId(now)),
                tenant.Id,
                command.Name,
                new CalendarTimeZone(command.TimeZone),
                now);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        if (command.Publish)
        {
            calendar.Publish();
        }

        await calendars.AddAsync(calendar, cancellationToken);
        return Result<CalendarId>.Success(calendar.Id);
    }
}
