using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>A tenant adds something a worker can be booked for.</summary>
public sealed class CreateServiceHandler(
    ITenantRepository tenants,
    IServiceRepository services,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ServiceId>> HandleAsync(CreateService command, CancellationToken cancellationToken)
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

        Service service;
        try
        {
            service = Service.Create(
                new ServiceId(idGenerator.NewId(clock.UtcNow)),
                tenant.Id,
                command.Name,
                // Minutes in, TimeSpan out, and the conversion happens exactly here. The wire carries
                // an int because a renderer prints "45 min"; the domain carries a TimeSpan because
                // that is what it does arithmetic in (date-and-time.md rule 7).
                TimeSpan.FromMinutes(command.DurationMinutes));
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        await services.AddAsync(service, cancellationToken);
        return Result<ServiceId>.Success(service.Id);
    }
}
