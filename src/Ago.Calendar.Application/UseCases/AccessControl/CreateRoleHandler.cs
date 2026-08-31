using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>
/// A tenant provisions a second role, on demand, from the console.
///
/// <para><b>Not seeded alongside the v1 role in provisioning, and that is a decision, not an
/// oversight.</b> <c>RegisterTenantHandler</c>'s own transaction creates exactly one role
/// (<see cref="Role.SeedOperatorRole"/>) for a real reason: every tenant that ever provisions gets it,
/// whether or not they will ever have a second person to put in a narrower one. Calling
/// <see cref="Role.Create"/> a second time inside that same transaction would give every tenant a role
/// nobody asked for - the opposite of `20-12`'s own goal, which is a role a tenant reaches for only
/// once they actually have someone to put in it.</para>
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/></b>, the same tier `18-08` chose for
/// its own analogous decision (<c>SiteConfigure</c> over a narrower read permission): creating a role
/// and moving an operator onto it is tenant management, not a read of tenant content, and this
/// product's nearest equivalent management-tier permission is the one every other provisioning-shaped
/// console route in <c>ConsoleEndpoints</c> already checks.</para>
/// </summary>
public sealed class CreateRoleHandler(
    IRoleRepository roles,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RoleId>> HandleAsync(CreateRole command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return AccessControlErrors.Forbidden(Permission.CalendarConfigure);
        }

        var existing = await roles.ListForTenantAsync(command.TenantId, cancellationToken);
        if (existing.Any(role => string.Equals(role.Name, command.Name?.Trim(), StringComparison.Ordinal)))
        {
            // ux_roles_tenant_name would refuse the insert anyway - checked here first so the caller
            // gets a clean access.invalid rather than a DbUpdateException surfacing as a 500.
            return AccessControlErrors.Invalid($"This tenant already has a role named '{command.Name}'.");
        }

        Role role;
        try
        {
            role = Role.Create(
                new RoleId(idGenerator.NewId(clock.UtcNow)), command.TenantId, command.Name, command.Permissions);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentNullException)
        {
            return AccessControlErrors.Invalid(exception.Message);
        }

        await roles.AddAsync(role, cancellationToken);
        return Result<RoleId>.Success(role.Id);
    }
}
