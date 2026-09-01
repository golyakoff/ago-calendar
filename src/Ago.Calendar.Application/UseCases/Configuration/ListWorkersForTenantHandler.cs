using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>`20-13`: <c>GET /workers</c>, the console's own table. Every worker of the tenant,
/// active and inactive alike - <see cref="IWorkerRepository.ListForTenantAsync"/>'s own remarks say
/// why hiding an inactive one would make deactivation look like deletion.</summary>
public sealed class ListWorkersForTenantHandler(IWorkerRepository workers, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<WorkerDetail>>> HandleAsync(
        ListWorkersForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var tenantWorkers = await workers.ListForTenantAsync(query.TenantId, cancellationToken);
        return Result<IReadOnlyList<WorkerDetail>>.Success(
            [.. tenantWorkers.Select(GetWorkerHandler.ToDetail)]);
    }
}
