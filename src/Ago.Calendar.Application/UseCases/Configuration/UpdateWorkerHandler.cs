using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `20-13`: <c>PUT /workers/{id}</c> - names, the optional custom display name, and activity.
///
/// <para><b>The order the two domain calls run in is the entire display-name-freezes-once-custom
/// guarantee, at this level.</b> <see cref="Worker.Rename"/> runs first and recomputes
/// <see cref="Worker.DisplayName"/> only if it is not already custom; <see cref="Worker.SetDisplayName"/>
/// runs second, only when the request actually carries an override, and it is what raises the flag.
/// Swapping the order would let a same-call rename silently overwrite an override the request just
/// asked for - see <c>WorkerTests</c> for the sequential proof at the aggregate's own level.</para>
/// </summary>
public sealed class UpdateWorkerHandler(
    IWorkerRepository workers,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsync(UpdateWorker command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("worker", command.WorkerId.Value);
        }

        var now = clock.UtcNow;
        try
        {
            worker.Rename(command.LastName, command.FirstName, command.MiddleName, now);

            if (command.DisplayName is not null)
            {
                worker.SetDisplayName(command.DisplayName, now);
            }
        }
        catch (ArgumentException exception)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        if (command.IsActive)
        {
            worker.Reactivate(now);
        }
        else
        {
            worker.Deactivate(now);
        }

        await workers.SaveAsync(worker, cancellationToken);
        return Result.Success();
    }
}
