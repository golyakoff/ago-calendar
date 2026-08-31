using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>
/// A tenant moves an operator off a role. The counterpart <see cref="Operator.Grant"/> never had a
/// caller for, and <see cref="Operator.Revoke"/>'s own guard clause is what stops this from being able
/// to strip the tenant's account owner down to no contact access at all - see
/// <see cref="AccountOwnerRoleException"/>.
/// </summary>
public sealed class RevokeOperatorRoleHandler(
    IOperatorRepository operators, IRoleRepository roles, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(RevokeOperatorRole command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AccessControlErrors.Forbidden(Permission.CalendarConfigure);
        }

        var target = await operators.GetByIdAsync(command.TargetOperatorId, cancellationToken);
        if (target is null || target.TenantId != command.TenantId)
        {
            return AccessControlErrors.OperatorNotFound(command.TargetOperatorId);
        }

        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken);
        if (role is null || role.TenantId != command.TenantId)
        {
            return AccessControlErrors.RoleNotFound(command.RoleId);
        }

        try
        {
            target.Revoke(role);
        }
        catch (AccountOwnerRoleException exception)
        {
            return AccessControlErrors.AccountOwnerRequiresContactAccess(exception.Message);
        }

        await operators.SaveAsync(target, cancellationToken);
        return Result.Success();
    }
}
