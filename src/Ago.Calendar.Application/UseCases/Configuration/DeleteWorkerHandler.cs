using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// `20-13`: <c>DELETE /workers/{id}</c>.
///
/// <para><b>The permission check and the "not found" check both run before the delete, deliberately
/// out of the atomic statement.</b> Neither is a race the way "was this worker ever booked" is -
/// they answer questions about the caller and about identity, not about concurrent writes to
/// <c>events</c> - so there is no gap for either of them to leave. Only the booking-history check
/// has to be inside the same statement as the delete, and
/// <see cref="IWorkerRepository.DeleteIfNeverBookedAsync"/> is where that happens.</para>
/// </summary>
public sealed class DeleteWorkerHandler(IWorkerRepository workers, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(DeleteWorker command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var deleted = await workers.DeleteIfNeverBookedAsync(command.WorkerId, command.TenantId, cancellationToken);
        if (deleted)
        {
            return Result.Success();
        }

        // Zero rows affected means one of three things: no such worker, one that belongs to another
        // tenant, or one with booking history. This follow-up read only decides how to word the
        // refusal - it changes nothing about what was or was not deleted, which the guarded DELETE
        // above already settled atomically, so reading it here after the fact cannot reopen the race
        // that method's own remarks describe.
        var worker = await workers.GetByIdAsync(command.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("worker", command.WorkerId.Value);
        }

        return ConfigurationErrors.WorkerHasBookingHistory(command.WorkerId);
    }
}
