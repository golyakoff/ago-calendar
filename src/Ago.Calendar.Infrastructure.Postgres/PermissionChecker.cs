using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-05`/`adr/0093`: resolves against <see cref="IRoleAssignmentProjectionStore"/>'s own projection
/// table now, not this product's former <c>operator_roles</c>/<c>roles</c> - see that port's own
/// remarks for the shape and for why a missing row means "refused", never "assume allowed".
///
/// <para><b>Still this product's own resolution</b>, in the sense <c>IPermissionChecker</c>'s own
/// remarks already drew: the permission <i>strings</i> are now the account side's shared catalogue
/// (`adr/0093`), but the <i>check</i> - does this operator, in this tenant, hold this string - still
/// runs against this product's own database, inside this product's own transaction (rule 8). Nothing
/// about adr/0016's model changed; only where the fact it reads from is written changed.</para>
/// </summary>
public sealed class PermissionChecker(IRoleAssignmentProjectionStore projections) : IPermissionChecker
{
    public async Task<bool> HasPermissionAsync(
        OperatorId operatorId, TenantId tenantId, Permission permission, CancellationToken cancellationToken)
    {
        var permissions = await projections.GetPermissionsAsync(operatorId, tenantId, cancellationToken);
        return permissions.Contains(permission.Value, StringComparer.Ordinal);
    }
}
