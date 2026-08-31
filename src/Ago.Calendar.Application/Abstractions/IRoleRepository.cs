using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Role"/> that `20-01`'s own remarks on <see cref="Role"/> named
/// as missing and left for later: "a v1 that could not configure its own calendar... a second,
/// narrower role... is the first thing a real multi-person tenant will need". `20-12` is that later.
///
/// <para><b>Why this port did not exist before now.</b> Until `20-12` nothing ever created a second
/// <see cref="Role"/> for a tenant - the only writer was <see cref="ITenantProvisioningStore"/>'s own
/// one-shot provisioning transaction, which persists exactly one seeded role alongside the tenant and
/// its first operator and never again. A tenant creating a role on demand, or an operator-assignment
/// screen reading back what roles exist, needed a port neither of those callers had any reason to
/// grow.</para>
/// </summary>
public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(RoleId id, CancellationToken cancellationToken);

    /// <summary>Every role of one tenant - what a role-assignment screen offers an operator, and what
    /// `20-12`'s own role-creation handler checks before adding a same-named one twice (`ux_roles_tenant_name`
    /// would refuse it at the database anyway; this is the check that turns that into a clean error rather
    /// than a `DbUpdateException`).</summary>
    Task<IReadOnlyList<Role>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task AddAsync(Role role, CancellationToken cancellationToken);
}
