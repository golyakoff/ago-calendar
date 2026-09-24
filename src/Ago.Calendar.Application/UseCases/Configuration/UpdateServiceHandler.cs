using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `26-96`: a tenant corrects a service, or takes it out of rotation.
///
/// <para><b>Shaped on <see cref="UpdateCalendarHandler"/>, deliberately.</b> Same permission, same
/// "load, check the tenant, mutate, save" order, and the same two-call body: the aggregate's own
/// <see cref="Service.Reconfigure"/> for the editable text, then
/// <see cref="Service.Deactivate"/>/<see cref="Service.Reactivate"/> for the flag. Both land in one
/// <see cref="IServiceRepository.SaveAsync"/>, so a correction and a withdrawal issued together are
/// one write rather than two a reader could observe half of.</para>
///
/// <para><b>The tenant check is a check, not a filter, and it answers with
/// <see cref="ConfigurationErrors.NotFound"/> rather than a forbidden.</b> An operator of tenant A
/// learning that a service id exists in tenant B is a cross-tenant leak however politely worded -
/// <see cref="ConfigurationErrors"/>'s own remarks state that rule, and this handler is one more
/// instance of it.</para>
///
/// <para><b>What this deliberately does not do.</b> It does not touch <c>worker_services</c>: a
/// worker who performs an archived service keeps performing it, so the worker card still names it and
/// a reactivation needs no second edit to put it back. It does not touch <c>events</c> either, for the
/// identical reason <see cref="UpdateCalendarHandler"/>'s own remarks give for unpublishing a
/// calendar: bookings already taken are appointments real people are expecting to keep, and a config
/// screen is not where cancelling them gets decided. What archiving actually stops is the *next*
/// booking - <c>BookingSurfaceReadStore</c> no longer lists the service, and <c>BookEventHandler</c>
/// refuses a claim that names it.</para>
/// </summary>
public sealed class UpdateServiceHandler(
    IServiceRepository services,
    IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(UpdateService command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var service = await services.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null || service.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("service", command.ServiceId.Value);
        }

        try
        {
            service.Reconfigure(
                command.Name,
                // The same minutes-in/TimeSpan-out conversion CreateServiceHandler performs, at the
                // identical boundary - date-and-time.md rule 7.
                TimeSpan.FromMinutes(command.DurationMinutes),
                // And the same kopecks-in/Money-out one, with v1's single currency chosen server-side
                // rather than accepted from the wire (Money's own remarks).
                command.PriceMinorUnits is { } minorUnits ? Money.Rubles(minorUnits) : null,
                command.PriceIsFrom,
                command.Description);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            // An operator typing "0" into the duration field is a caller mistake, not a fault - the
            // same translation CreateServiceHandler makes, so an edit refuses exactly where a create
            // already refuses and with the domain's own sentence.
            return ConfigurationErrors.Invalid(exception.Message);
        }

        if (command.IsActive)
        {
            service.Reactivate();
        }
        else
        {
            service.Deactivate();
        }

        await services.SaveAsync(service, cancellationToken);
        return Result.Success();
    }
}
