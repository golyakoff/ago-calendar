using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// Resolves whether an operator holds a permission within a tenant (adr/0016's pattern).
///
/// <para><b>This product's own catalogue, resolved against this product's own tables.</b> adr/0027 is
/// explicit that there is no shared <c>Permission</c> enum and no shared <c>roles</c> table across
/// the two products: a permission granted in AGO Chat's console grants nothing here, and this port
/// can never see a Chat role because the row lives in a database this product cannot reach. The
/// shape is copied from <c>Ago.Chat.Application.Abstractions.IPermissionChecker</c>; nothing else
/// is.</para>
///
/// <para><b>Scoped by tenant, always.</b> Every method takes one, because a permission in this model
/// is granted per tenant and a check without one could only be scoped to something a handler
/// invented. AGO Chat's own <c>TenantScopeTests</c> exists to catch exactly that mistake, and the
/// signature is what makes it hard to make here.</para>
///
/// <para><b>Customers never go through this port.</b> A customer holds no role and no login at all -
/// the booking endpoint (`20-03`) is unauthenticated by design. Only an operator is ever the subject
/// of a check.</para>
/// </summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(
        OperatorId operatorId, TenantId tenantId, Permission permission, CancellationToken cancellationToken);
}
