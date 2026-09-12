using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Calendar.Application.Realtime;

/// <summary>
/// `25-63`: the one place this product decides how its own identities map onto the platform's opaque
/// <see cref="PrincipalKey"/> (`Ago.Chat.Application.Realtime.PrincipalKeys`' own precedent, reused
/// rather than reinvented - `IConnectionRegistry`/`INodeFanoutPublisher` never know a tenant or an
/// operator exists, per clean-architecture.md's qualifying rule).
///
/// <para><b>Keyed by tenant, not by operator.</b> AGO Chat keys by <c>OperatorId</c> because a
/// conversation push has one specific recipient (the assigned operator, or a visitor). This product's
/// one push - "the pending queue changed" - has no such recipient: every operator of the tenant who
/// can see the queue needs it, and this codebase carries no per-tenant operator directory to resolve
/// "every operator" from (`22-05`/`adr/0093` removed the local <c>operators</c> table; identity is
/// Keycloak's, and calendar tenancy is a projection of a grant, not a roster this product can list -
/// <c>IRoleAssignmentProjectionStore</c>'s own shape has no "list every operator of a tenant" query,
/// because nothing before this item needed one). Registering every connection under one key that
/// names the *tenant* rather than the operator sidesteps needing that directory at all: the platform's
/// connection registry already knows which connections exist under a key, so "every operator connected
/// to this tenant's calendar hub right now" falls out of <c>CalendarOperatorHub.OnConnectedAsync</c>
/// registering under this key, with no separate roster to keep in step.</para>
/// </summary>
public static class PrincipalKeys
{
    private const string TenantPrefix = "tenant:";

    public static PrincipalKey ForTenant(TenantId tenantId) => new($"{TenantPrefix}{tenantId.Value}");
}
