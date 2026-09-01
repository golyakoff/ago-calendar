using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>
/// `adr/0088`'s one real gap, closed: until now <see cref="Operator.Create"/> had exactly one caller
/// (<c>RegisterTenantHandler</c>, for the account owner), so a tenant had no way to add a second
/// operator at all. This is that second caller - deliberate, on demand, from the console, the same
/// on-demand shape `20-12`'s <see cref="CreateRoleHandler"/> already established for a second role.
///
/// <para><b>Creates the row; never links it.</b> <see cref="Operator.ExternalSubjectId"/> stays null -
/// linking is <c>OperatorIdentityClaimsTransformation</c>'s own job, on a later, deliberate sign-in.
/// Keeping the two apart is the literal shape of `20-08`'s own Done-when: no path may create <i>or</i>
/// mutate an operator as a side effect of acting on a booking, and an invite is neither of those - it
/// is its own deliberate act, gated the same way every other console provisioning route already
/// is.</para>
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/></b>, the same tier every other
/// tenant-management route in <c>ConsoleEndpoints</c> already checks - inviting a colleague is tenant
/// management, not a read of tenant content.</para>
/// </summary>
public sealed class InviteOperatorHandler(
    IOperatorRepository operators,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<OperatorId>> HandleAsync(InviteOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AccessControlErrors.Forbidden(Permission.CalendarConfigure);
        }

        Operator invited;
        try
        {
            invited = Operator.Create(
                new OperatorId(idGenerator.NewId(clock.UtcNow)),
                command.TenantId,
                command.DisplayName,
                invitedEmail: new InvitedEmail(command.Email));
        }
        catch (ArgumentException exception)
        {
            return AccessControlErrors.Invalid(exception.Message);
        }

        await operators.AddAsync(invited, cancellationToken);
        return Result<OperatorId>.Success(invited.Id);
    }
}
