namespace Ago.Calendar.Domain;

/// <summary>
/// One row of <c>operator_roles</c>, owned by the <see cref="Operator"/> aggregate. A join row with
/// no identity of its own: it says nothing except that these two ids are related, so it gets no
/// surrogate key and no behaviour.
/// </summary>
public sealed class RoleAssignment
{
    public OperatorId OperatorId { get; private set; }

    public RoleId RoleId { get; private set; }

    /// <summary>
    /// A snapshot, taken at <see cref="Operator.Grant"/> time, of whether the granted
    /// <see cref="Role"/> included <see cref="Permission.CustomerRead"/> (`20-12`). Safe to snapshot
    /// rather than recompute on every check: nothing in this codebase ever changes an existing role's
    /// permission set after <see cref="Role.Create"/>/<see cref="Role.SeedOperatorRole"/> construct it,
    /// so the value cannot go stale.
    ///
    /// <para>Exists so <see cref="Operator"/>'s own account-owner invariant can be enforced from
    /// inside the aggregate itself, without loading every other <see cref="Role"/> the operator holds
    /// on every mutation. That mirrors the exact reasoning <see cref="Operator.Grant"/>'s own remarks
    /// give for taking the whole <see cref="Role"/> rather than a bare <see cref="RoleId"/>: if the
    /// check instead required a caller to pass in "all the roles this operator currently holds", the
    /// invariant would stop being something the aggregate enforces and become a convention every
    /// future call site has to remember to satisfy.</para>
    /// </summary>
    public bool GrantsCustomerRead { get; private set; }

    internal RoleAssignment(OperatorId operatorId, RoleId roleId, bool grantsCustomerRead)
    {
        OperatorId = operatorId;
        RoleId = roleId;
        GrantsCustomerRead = grantsCustomerRead;
    }

    // EF Core materialization only - never called by domain code.
    private RoleAssignment()
    {
    }
}
