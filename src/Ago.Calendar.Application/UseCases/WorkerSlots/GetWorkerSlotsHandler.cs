using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.WorkerSlots;

/// <param name="From">Inclusive.</param>
/// <param name="To">Inclusive.</param>
public readonly record struct GetWorkerSlots(
    OperatorId OperatorId, TenantId TenantId, WorkerId WorkerId, DateOnly From, DateOnly To);

/// <summary>
/// `20-15`: the materialised slot view. The tenant's own configuration screen for "what did my
/// schedule actually produce", which today only exists from the customer's side of the public
/// widget - see <see cref="IWorkerSlotReadStore"/> for the rest of that argument.
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/>, with
/// <see cref="Permission.CustomerRead"/> layered on top for the contact columns only - the item's own
/// Decided section, and the identical two-layer shape `20-12` already gave the pending-bookings
/// queue.</b> This is a configuration screen (whoever sets schedules can check them), so the first
/// gate is the screen itself; the second is what stops a config-but-no-contacts role from reading
/// here the phone numbers `20-12` withheld from them one screen away.</para>
///
/// <para><b>The actor check comes before the shape check, which comes before the existence check</b> -
/// the same order <c>EditDayBoundaryHandler</c> uses and for the identical reason: a caller who may
/// not act here learns nothing about whether their range was well formed or whether the worker id
/// they guessed exists.</para>
/// </summary>
public sealed class GetWorkerSlotsHandler(
    IWorkerSlotReadStore slots, IWorkerRepository workers, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<WorkerSlotRow>>> HandleAsync(
        GetWorkerSlots query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return WorkerSlotsErrors.Forbidden(Permission.CalendarConfigure);
        }

        if (query.To < query.From)
        {
            return WorkerSlotsErrors.InvalidRange(query.From, query.To);
        }

        var worker = await workers.GetByIdAsync(query.WorkerId, cancellationToken);
        if (worker is null || worker.TenantId != query.TenantId)
        {
            return WorkerSlotsErrors.WorkerNotFound(query.WorkerId);
        }

        // `20-12`: a *second*, independent permission check - never a reason to refuse the whole
        // read, only whether the row's contact fields are populated. Re-resolved on every request
        // against this caller's real, current roles, the same reasoning
        // `GetPendingBookingsForTenantHandler` gives for its own identical second check.
        var canReadContacts = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);

        var rows = await slots.GetForWorkerAsync(
            query.TenantId, query.WorkerId, query.From, query.To, canReadContacts, cancellationToken);
        return Result<IReadOnlyList<WorkerSlotRow>>.Success(rows);
    }
}
