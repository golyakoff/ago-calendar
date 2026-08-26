using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0016's resolution, against this product's own <c>operator_roles</c> and <c>roles</c> - see
/// <see cref="IPermissionChecker"/> for why there is no sharing with AGO Chat's identical-looking
/// tables.
///
/// <para><b>The tenant filter is on <c>roles</c>, not on <c>operators</c>, and that is the load-
/// bearing half.</b> An operator belongs to one tenant structurally, but the question being asked is
/// "may this operator act <i>on this tenant's</i> bookings" - and answering it by checking the
/// operator's own <c>tenant_id</c> would trust a value the caller's own token supplied. Filtering the
/// roles by the tenant the *action* names means a token claiming another tenant resolves to no roles
/// at all rather than to its own.</para>
/// </summary>
public sealed class PermissionChecker(AgoCalendarDbContext db) : IPermissionChecker
{
    public async Task<bool> HasPermissionAsync(
        OperatorId operatorId, TenantId tenantId, Permission permission, CancellationToken cancellationToken)
    {
        // One query for the roles, then membership in memory through Role.Grants.
        //
        // Not a translated `permissions @> ARRAY[...]`, and the reason is a real limit rather than a
        // preference: `roles.permissions` is a `text[]` behind a value converter (`20-01`), and EF
        // cannot see through a converter to translate containment on the element type - it fails at
        // runtime with "could not be translated", which is how this was found. ago-chat's own checker
        // translates because its column is a plain `string[]` with no converter; this product kept the
        // strongly-typed Permission, and this is the price.
        //
        // Cheap anyway, and worth stating rather than apologising for: an operator holds one role in
        // v1 and a role holds seven permissions, so what is materialised is a handful of short
        // strings. Testing membership through <see cref="Role.Grants"/> also means the aggregate
        // stays the one definition of what a role grants, instead of a second copy of that rule
        // living in SQL.
        var roles = await db.Roles
            .Where(role => role.TenantId == tenantId
                && db.Set<RoleAssignment>().Any(assignment =>
                    assignment.OperatorId == operatorId && assignment.RoleId == role.Id))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return roles.Exists(role => role.Grants(permission));
    }
}
