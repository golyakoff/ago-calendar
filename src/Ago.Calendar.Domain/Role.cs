namespace Ago.Calendar.Domain;

/// <summary>
/// A tenant-scoped bundle of <see cref="Permission"/>s (adr/0016's shape, this product's own
/// vocabulary per adr/0027).
///
/// <para><b>The v1 seeded role set, stated once and explicitly</b> - mirroring `1-05`'s single
/// hardcoded <c>"Operator"</c> role for AGO Chat's Stage 1: <b>exactly one role,
/// <see cref="OperatorRoleName"/>, holding every permission in the catalogue</b>
/// (<see cref="OperatorPermissions"/>). Including <see cref="Permission.CalendarConfigure"/> is a
/// decision, not laziness: the product spec's own framing is that in a small business one person is
/// the tenant, the operator and the only worker at once, so a v1 that could not configure its own
/// calendar from the operator login would be unusable by its own target customer. A second, narrower
/// role (a dispatcher who confirms bookings but cannot reshape the schedule) is the first thing a
/// real multi-person tenant will need, and adr/0016's granular permissions are what makes adding it
/// a data change rather than a code change.</para>
///
/// <para><b>Who writes these rows today: nothing in production yet.</b> The tables exist and this
/// factory produces the seed, but the provisioning transaction that calls it belongs to `20-06`,
/// the same position AGO Chat's own <c>roles</c> table was in at `1-04` (its seed script arrived in
/// `1-05`). Said plainly rather than papered over with a speculative use case.</para>
/// </summary>
public sealed class Role
{
    public const string OperatorRoleName = "Operator";

    private readonly List<Permission> _permissions = [];

    public RoleId Id { get; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;

    public IReadOnlyList<Permission> Permissions => _permissions;

    private Role(RoleId id, TenantId tenantId, string name, IEnumerable<Permission> permissions)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        _permissions.AddRange(permissions);
    }

    // EF Core materialization only - never called by domain code.
    private Role()
    {
    }

    /// <summary>The v1 permission set - see the type's own remarks for why it is the whole
    /// catalogue.</summary>
    public static IReadOnlyList<Permission> OperatorPermissions { get; } =
    [
        Permission.BookingConfirm,
        Permission.BookingReject,
        Permission.BookingCancel,
        Permission.BookingMarkNoShow,
        Permission.CustomerRead,
        Permission.CustomerEdit,
        Permission.CalendarConfigure,
    ];

    public static Role SeedOperatorRole(RoleId id, TenantId tenantId) =>
        new(id, tenantId, OperatorRoleName, OperatorPermissions);

    public static Role Create(RoleId id, TenantId tenantId, string name, IEnumerable<Permission> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(permissions);

        var granted = permissions.Distinct().ToList();
        if (granted.Count == 0)
        {
            throw new ArgumentException("A role that grants nothing is not a role.", nameof(permissions));
        }

        return new Role(id, tenantId, name.Trim(), granted);
    }

    public bool Grants(Permission permission) => _permissions.Contains(permission);
}
