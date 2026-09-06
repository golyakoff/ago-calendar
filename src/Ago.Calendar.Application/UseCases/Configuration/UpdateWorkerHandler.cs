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
///
/// <para><b>`22-23`: a false-to-true transition on <see cref="Worker.IsActive"/> is a reactivation,
/// and only that transition is gated by the quota.</b> <c>wasActive</c> is read before either domain
/// call below can change it, precisely so an already-active worker saved with <c>IsActive</c> still
/// <see langword="true"/> - an ordinary rename on the console's edit form, which resends the worker's
/// current activity alongside whatever the human actually changed - is never mistaken for a
/// reactivation and refused for a quota it was never leaving. Gating every call where
/// <c>command.IsActive</c> is true, without checking the transition, would refuse that ordinary edit
/// the instant a tenant sits exactly at its quota - a real bug this comment exists to keep out.</para>
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

        // Read before either call below can change it - see this class's own remarks on why the
        // transition, not the requested end state, is what decides whether the quota applies.
        var isReactivation = command.IsActive && !worker.IsActive;

        if (command.IsActive)
        {
            worker.Reactivate(now);
        }
        else
        {
            worker.Deactivate(now);
        }

        if (isReactivation)
        {
            // `22-23`/`adr/0125`: the same lock-and-count `TryAddWithinQuotaAsync` uses for creation,
            // not a second shape for the same invariant - see IWorkerRepository's own remarks. This
            // call opens and commits its own transaction, so a refusal here leaves the rename above
            // unpersisted too, matching this class's "nothing written" contract for a refused
            // reactivation.
            var accepted = await workers.TryReactivateWithinQuotaAsync(worker, cancellationToken);
            if (!accepted)
            {
                return ConfigurationErrors.WorkerQuotaExceeded(command.TenantId);
            }
        }
        else
        {
            await workers.SaveAsync(worker, cancellationToken);
        }

        return Result.Success();
    }
}
