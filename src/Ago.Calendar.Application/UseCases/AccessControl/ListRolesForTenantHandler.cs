using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>Every role of one tenant - the role-assignment screen's own picker, and
/// <see cref="CreateRoleHandler"/>'s own duplicate-name check reused as a read. Gated the same as
/// <see cref="CreateRoleHandler"/>: seeing what roles exist is the same management tier as creating
/// one.</summary>
public sealed class ListRolesForTenantHandler(IRoleRepository roles, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<Role>>> HandleAsync(
        ListRolesForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AccessControlErrors.Forbidden(Permission.CalendarConfigure);
        }

        var rows = await roles.ListForTenantAsync(query.TenantId, cancellationToken);
        return Result<IReadOnlyList<Role>>.Success(rows);
    }
}
