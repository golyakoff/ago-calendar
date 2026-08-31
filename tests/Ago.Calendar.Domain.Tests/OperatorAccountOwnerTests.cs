namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-12`'s own account-owner invariant: <see cref="Operator.IsAccountOwner"/> always holds a role
/// granting <see cref="Permission.CustomerRead"/>, enforced from inside <see cref="Operator.Grant"/>
/// and <see cref="Operator.Revoke"/> rather than by a console that merely discourages stripping it
/// away. No database here, on purpose, the same reasoning <c>EventStateMachineTests</c>' own remarks
/// give: this is a rule about one aggregate, provable in microseconds without one.
/// </summary>
public class OperatorAccountOwnerTests
{
    private static readonly DateTimeOffset Now = CalendarFixtures.Now;

    [Fact]
    public void TheAccountOwner_CannotBeRevokedDownToNoContactAccess()
    {
        // The demonstration the item asks for: an actual attempt to strip the owner, not merely a
        // check that they start out holding it.
        var tenant = CalendarFixtures.Tenant();
        var role = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var owner = Operator.Create(new OperatorId(NewId()), tenant.Id, "Sam", isAccountOwner: true);
        owner.Grant(role);

        var exception = Assert.Throws<AccountOwnerRoleException>(() => owner.Revoke(role));

        Assert.Contains(Permission.CustomerRead.Value, exception.Message, StringComparison.Ordinal);
        // Refused, not merely reported: the role is still held afterwards.
        Assert.Contains(owner.Roles, assignment => assignment.RoleId == role.Id);
    }

    [Fact]
    public void TheAccountOwner_CanBeRevokedDownToADifferentRole_IfThatOneAlsoGrantsCustomerRead()
    {
        var tenant = CalendarFixtures.Tenant();
        var seeded = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var alsoReads = Role.Create(new RoleId(NewId()), tenant.Id, "Also reads", [Permission.CustomerRead]);
        var owner = Operator.Create(new OperatorId(NewId()), tenant.Id, "Sam", isAccountOwner: true);
        owner.Grant(seeded);
        owner.Grant(alsoReads);

        owner.Revoke(seeded);

        Assert.DoesNotContain(owner.Roles, assignment => assignment.RoleId == seeded.Id);
        Assert.Contains(owner.Roles, assignment => assignment.RoleId == alsoReads.Id);
    }

    [Fact]
    public void TheAccountOwner_CannotBeGrantedOnlyANonContactRole_AsTheirFirstRole()
    {
        // Grant is monotonic - it can only violate the invariant in one real sequence: the owner
        // holds nothing yet, and the very first role granted lacks CustomerRead too.
        var tenant = CalendarFixtures.Tenant();
        var dispatcher = Role.Create(
            new RoleId(NewId()), tenant.Id, "Dispatcher", [Permission.BookingReject, Permission.BookingCancel]);
        var owner = Operator.Create(new OperatorId(NewId()), tenant.Id, "Sam", isAccountOwner: true);

        var exception = Assert.Throws<AccountOwnerRoleException>(() => owner.Grant(dispatcher));

        Assert.Contains(Permission.CustomerRead.Value, exception.Message, StringComparison.Ordinal);
        Assert.Empty(owner.Roles);
    }

    [Fact]
    public void TheAccountOwner_CanHoldAnAdditionalNonContactRole_AlongsideOneThatGrantsCustomerRead()
    {
        var tenant = CalendarFixtures.Tenant();
        var seeded = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var dispatcher = Role.Create(
            new RoleId(NewId()), tenant.Id, "Dispatcher", [Permission.BookingReject, Permission.BookingCancel]);
        var owner = Operator.Create(new OperatorId(NewId()), tenant.Id, "Sam", isAccountOwner: true);
        owner.Grant(seeded);

        owner.Grant(dispatcher);

        Assert.Equal(2, owner.Roles.Count);
    }

    [Fact]
    public void ANonOwner_CanBeRevokedDownToNoRolesAtAll()
    {
        // The invariant is scoped to the account owner alone - an ordinary operator has no such
        // guarantee, and Revoke must not invent one for them.
        var tenant = CalendarFixtures.Tenant();
        var role = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var ordinary = Operator.Create(new OperatorId(NewId()), tenant.Id, "Robin");
        ordinary.Grant(role);

        ordinary.Revoke(role);

        Assert.Empty(ordinary.Roles);
    }

    [Fact]
    public void Revoke_ARoleFromAnotherTenant_ThrowsTenantMismatch()
    {
        var tenant = CalendarFixtures.Tenant();
        var otherTenant = CalendarFixtures.Tenant();
        var foreignRole = Role.SeedOperatorRole(new RoleId(NewId()), otherTenant.Id);
        var @operator = Operator.Create(new OperatorId(NewId()), tenant.Id, "Robin");

        Assert.Throws<TenantMismatchException>(() => @operator.Revoke(foreignRole));
    }

    [Fact]
    public void Revoke_ARoleNeverHeld_IsANoOp()
    {
        var tenant = CalendarFixtures.Tenant();
        var role = Role.SeedOperatorRole(new RoleId(NewId()), tenant.Id);
        var @operator = Operator.Create(new OperatorId(NewId()), tenant.Id, "Robin");

        @operator.Revoke(role);

        Assert.Empty(@operator.Roles);
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);
}
