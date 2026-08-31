namespace Ago.Calendar.Domain;

/// <summary>
/// Refused when a role grant or revoke on <see cref="Operator.IsAccountOwner"/>'s own operator would
/// leave them holding no role that grants <see cref="Permission.CustomerRead"/> (`20-12`).
///
/// <para>Named and shaped after <see cref="TenantMismatchException"/> on purpose: both exist to make a
/// multi-tenancy-adjacent rule an invariant the aggregate itself refuses to violate, rather than a
/// convention a caller has to remember. "The tenant's own account owner always sees contact data" is
/// only true if nothing - not a careless revoke, not a role reassignment - can strip it away silently;
/// an exception thrown from inside <see cref="Operator.Grant"/>/<see cref="Operator.Revoke"/> is what
/// makes that a guarantee instead of a UI convention that a direct API call could bypass.</para>
/// </summary>
public sealed class AccountOwnerRoleException(string message) : InvalidOperationException(message);
