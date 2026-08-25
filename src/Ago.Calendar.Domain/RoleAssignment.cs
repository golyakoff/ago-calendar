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

    internal RoleAssignment(OperatorId operatorId, RoleId roleId)
    {
        OperatorId = operatorId;
        RoleId = roleId;
    }

    // EF Core materialization only - never called by domain code.
    private RoleAssignment()
    {
    }
}
