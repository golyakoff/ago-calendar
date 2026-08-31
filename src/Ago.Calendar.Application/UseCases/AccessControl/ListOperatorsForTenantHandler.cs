using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>Every operator of one tenant, roles included - what the role-assignment screen renders as
/// rows to grant or revoke against. Same permission tier as the rest of this feature's own
/// handlers.</summary>
public sealed class ListOperatorsForTenantHandler(IOperatorRepository operators, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<Operator>>> HandleAsync(
        ListOperatorsForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AccessControlErrors.Forbidden(Permission.CalendarConfigure);
        }

        var rows = await operators.ListForTenantAsync(query.TenantId, cancellationToken);
        return Result<IReadOnlyList<Operator>>.Success(rows);
    }
}
