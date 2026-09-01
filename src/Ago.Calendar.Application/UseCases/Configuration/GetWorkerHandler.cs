using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>`20-13`: everything the worker card needs to prefill an edit form, and every row the
/// workers table renders. One shape for both - see <see cref="ListWorkersForTenantHandler"/>, which
/// returns a list of the same record, so the console never needs a second round trip to open a card
/// for a worker it already listed.</summary>
public readonly record struct WorkerDetail(
    WorkerId WorkerId,
    string LastName,
    string FirstName,
    string? MiddleName,
    string DisplayName,
    bool DisplayNameIsCustom,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>`20-13`: <c>GET /workers/{id}</c>. Gated on the same <see cref="Permission.CalendarConfigure"/>
/// every other configuration screen is - a worker's split name fields are not secret, but neither is
/// the tenant's allowed-origin list, and that one is gated too (<c>GetTenantConfigurationHandler</c>'s
/// own remarks say why: not a secret, but not a thing to give away either).</summary>
public sealed class GetWorkerHandler(IWorkerRepository workers, IPermissionChecker permissions)
{
    public async Task<Result<WorkerDetail>> HandleAsync(GetWorker query, CancellationToken cancellationToken)
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

        return ToDetail(worker);
    }

    internal static WorkerDetail ToDetail(Worker worker) => new(
        worker.Id,
        worker.LastName,
        worker.FirstName,
        worker.MiddleName,
        worker.DisplayName,
        worker.DisplayNameIsCustom,
        worker.IsActive,
        worker.CreatedAt,
        worker.UpdatedAt);
}
