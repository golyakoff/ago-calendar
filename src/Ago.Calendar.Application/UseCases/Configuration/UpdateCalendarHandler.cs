using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// Renames a calendar, changes its buffer, and publishes or unpublishes it.
///
/// <para><b>Unpublishing does not touch a single row in <c>events</c>, and that is deliberate.</b>
/// An unpublished calendar disappears from the public surface (<c>BookEventHandler</c> and
/// <c>EmbedScopeResolver</c> both refuse it), so nothing new can be booked - but the bookings already
/// taken are appointments real people are expecting to keep. Cancelling them as a side effect of a
/// checkbox would be the loudest possible violation of the rule the rest of this product observes
/// everywhere: a customer whose booking goes away has to be told, and a config screen is not where
/// that gets decided.</para>
/// </summary>
public sealed class UpdateCalendarHandler(
    IBookingCalendarRepository calendars,
    IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(UpdateCalendar command, CancellationToken cancellationToken)
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

        try
        {
            calendar.Reconfigure(command.Name);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        if (command.Publish)
        {
            calendar.Publish();
        }
        else
        {
            calendar.Unpublish();
        }

        await calendars.SaveAsync(calendar, cancellationToken);
        return Result.Success();
    }
}
