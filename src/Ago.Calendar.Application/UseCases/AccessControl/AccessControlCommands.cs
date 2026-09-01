using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>
/// `20-12`'s own surface gap, named by the item file: "only one role is ever seeded... nothing calls
/// <c>Role.Create</c> a second time, and no surface lets a tenant create a role or reassign an
/// operator's roles". These five commands are that surface. Deliberately on-demand rather than seeded
/// alongside <see cref="Role.SeedOperatorRole"/> in provisioning - see <c>CreateRoleHandler</c>'s own
/// remarks for why.
/// </summary>
/// <param name="Permissions">Any non-empty subset of the catalogue - <see cref="Role.Create"/> is
/// already fully general, so this command adds no rule of its own beyond passing the caller's choice
/// through.</param>
public readonly record struct CreateRole(
    OperatorId OperatorId, TenantId TenantId, string Name, IReadOnlyList<Permission> Permissions);

public readonly record struct ListRolesForTenant(OperatorId OperatorId, TenantId TenantId);

public readonly record struct ListOperatorsForTenant(OperatorId OperatorId, TenantId TenantId);

/// <param name="TargetOperatorId">Whose roles change - separate from <see cref="OperatorId"/>, the
/// caller, the same "who is acting" vs "who is acted on" split `20-04`'s own commands already
/// draw.</param>
public readonly record struct GrantOperatorRole(
    OperatorId OperatorId, TenantId TenantId, OperatorId TargetOperatorId, RoleId RoleId);

public readonly record struct RevokeOperatorRole(
    OperatorId OperatorId, TenantId TenantId, OperatorId TargetOperatorId, RoleId RoleId);

/// <summary>
/// `adr/0088`: a tenant invites a colleague onto the Access screen. <see cref="DisplayName"/> and
/// <see cref="Email"/> are the only two fields the interface asks for - the decision's own wording is
/// "nothing else" - so this command carries nothing an account owner's own provisioning path does not
/// already carry a version of.
/// </summary>
public readonly record struct InviteOperator(
    OperatorId OperatorId, TenantId TenantId, string DisplayName, string Email);
