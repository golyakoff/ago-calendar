using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>
/// A tenant moves an operator onto a role. `20-12`'s own missing half of "the gap is thinner than it
/// first looks" - <see cref="Operator.Grant"/> already did everything the aggregate needs to; nothing
/// ever called it from outside a provisioning transaction before this.
/// </summary>
public sealed class GrantOperatorRoleHandler(
    IOperatorRepository operators, IRoleRepository roles, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(GrantOperatorRole command, CancellationToken cancellationToken)
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
            // Absent rather than forbidden for the cross-tenant case - the same leak-avoidance call
            // BookingLifecycleErrors.WrongTenant already makes.
            return AccessControlErrors.OperatorNotFound(command.TargetOperatorId);
        }

        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken);
        if (role is null || role.TenantId != command.TenantId)
        {
            return AccessControlErrors.RoleNotFound(command.RoleId);
        }

        try
        {
            target.Grant(role);
        }
        catch (AccountOwnerRoleException exception)
        {
            return AccessControlErrors.AccountOwnerRequiresContactAccess(exception.Message);
        }

        await operators.SaveAsync(target, cancellationToken);
        return Result.Success();
    }
}
