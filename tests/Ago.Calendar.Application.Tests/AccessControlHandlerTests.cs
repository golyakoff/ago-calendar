using Ago.Calendar.Application.UseCases.AccessControl;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-12`'s own surface: creating a second role, listing roles/operators, and moving an operator
/// on/off a role - every port faked, the same shape <c>BookingLifecycleHandlerTests</c> already
/// established for the three operator-facing transitions.
/// </summary>
public class AccessControlHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateRole_Succeeds_AndPersistsAnyPermissionSubset()
    {
        var world = new World();

        var result = await world.CreateRoleAsync("Dispatcher", [Permission.BookingReject, Permission.BookingCancel]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var added = Assert.Single(world.Roles.Added);
        Assert.Equal("Dispatcher", added.Name);
        Assert.Equal(2, added.Permissions.Count);
    }

    [Fact]
    public async Task CreateRole_WithoutCalendarConfigure_IsRefusedAndWritesNothing()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.CreateRoleAsync("Dispatcher", [Permission.BookingReject]);

        Assert.Equal("access.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Roles.Added);
    }

    [Fact]
    public async Task CreateRole_WithADuplicateName_IsRejectedBeforeTouchingTheStore()
    {
        var existing = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var world = new World(existing);

        var result = await world.CreateRoleAsync(Role.OperatorRoleName, [Permission.BookingReject]);

        Assert.Equal("access.invalid", result.Error!.Value.Code);
        Assert.Empty(world.Roles.Added);
    }

    [Fact]
    public async Task ListRoles_WithoutCalendarConfigure_IsRefused()
    {
        var world = new World(Role.SeedOperatorRole(new RoleId(NewId()), TenantId));
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.ListRolesAsync();

        Assert.Equal("access.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task ListRoles_ReturnsOnlyThisTenantsRoles()
    {
        var mine = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var theirs = Role.SeedOperatorRole(new RoleId(NewId()), new TenantId(NewId()));
        var world = new World(mine, theirs);

        var result = await world.ListRolesAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(mine.Id, Assert.Single(result.Value).Id);
    }

    [Fact]
    public async Task GrantOperatorRole_MovesTwoRealOperatorsOntoDifferentRoles()
    {
        // The item's own Done-when wording: "two real operators ending up with different roles".
        var seeded = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var dispatcherRole = Role.Create(
            new RoleId(NewId()), TenantId, "Dispatcher", [Permission.BookingReject, Permission.BookingCancel]);
        var owner = Operator.Create(new OperatorId(NewId()), TenantId, "Owner", isAccountOwner: true);
        owner.Grant(seeded);
        var junior = Operator.Create(new OperatorId(NewId()), TenantId, "Junior");

        var world = new World([seeded, dispatcherRole], [owner, junior]);

        var result = await world.GrantAsync(junior.Id, dispatcherRole.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var saved = Assert.Single(world.Operators.Saved);
        Assert.Equal(junior.Id, saved.Id);
        Assert.Contains(saved.Roles, a => a.RoleId == dispatcherRole.Id);
        Assert.DoesNotContain(saved.Roles, a => a.RoleId == seeded.Id);

        // The owner is untouched - two operators, two different role sets.
        Assert.DoesNotContain(owner.Roles, a => a.RoleId == dispatcherRole.Id);
    }

    [Fact]
    public async Task GrantOperatorRole_ToAnotherTenantsOperator_IsReportedAsAbsent()
    {
        var role = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var foreignOperator = Operator.Create(new OperatorId(NewId()), new TenantId(NewId()), "Someone Else");
        var world = new World([role], [foreignOperator]);

        var result = await world.GrantAsync(foreignOperator.Id, role.Id);

        Assert.Equal("access.not_found", result.Error!.Value.Code);
        Assert.Empty(world.Operators.Saved);
    }

    [Fact]
    public async Task GrantOperatorRole_WithoutCalendarConfigure_IsRefusedAndWritesNothing()
    {
        var role = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var target = Operator.Create(new OperatorId(NewId()), TenantId, "Robin");
        var world = new World([role], [target]);
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.GrantAsync(target.Id, role.Id);

        Assert.Equal("access.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Operators.Saved);
    }

    [Fact]
    public async Task RevokeOperatorRole_FromAnOrdinaryOperator_Succeeds()
    {
        var role = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var target = Operator.Create(new OperatorId(NewId()), TenantId, "Robin");
        target.Grant(role);
        var world = new World([role], [target]);

        var result = await world.RevokeAsync(target.Id, role.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(Assert.Single(world.Operators.Saved).Roles);
    }

    [Fact]
    public async Task RevokeOperatorRole_TheAccountOwnersOnlyContactRole_IsRefused()
    {
        // The demonstration the item asks for: a real attempt through the handler surface, not just
        // the Domain-level test.
        var role = Role.SeedOperatorRole(new RoleId(NewId()), TenantId);
        var owner = Operator.Create(new OperatorId(NewId()), TenantId, "Owner", isAccountOwner: true);
        owner.Grant(role);
        var caller = Operator.Create(Caller, TenantId, "Admin");
        var world = new World([role], [owner, caller]);

        var result = await world.RevokeAsync(owner.Id, role.Id);

        Assert.Equal("access.account_owner_requires_contact_access", result.Error!.Value.Code);
        Assert.Empty(world.Operators.Saved);
        Assert.Contains(owner.Roles, a => a.RoleId == role.Id);
    }

    [Fact]
    public async Task ListOperators_WithoutCalendarConfigure_IsRefused()
    {
        var world = new World([], [Operator.Create(new OperatorId(NewId()), TenantId, "Robin")]);
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.ListOperatorsAsync();

        Assert.Equal("access.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task ListOperators_ReturnsOnlyThisTenantsOperators()
    {
        var mine = Operator.Create(new OperatorId(NewId()), TenantId, "Robin");
        var theirs = Operator.Create(new OperatorId(NewId()), new TenantId(NewId()), "Someone Else");
        var world = new World([], [mine, theirs]);

        var result = await world.ListOperatorsAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(mine.Id, Assert.Single(result.Value).Id);
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);

    private sealed class World
    {
        private readonly CreateRoleHandler _createRole;
        private readonly ListRolesForTenantHandler _listRoles;
        private readonly ListOperatorsForTenantHandler _listOperators;
        private readonly GrantOperatorRoleHandler _grant;
        private readonly RevokeOperatorRoleHandler _revoke;

        public World(params Role[] roles) : this(roles, [])
        {
        }

        public World(Role[] roles, Operator[] operators)
        {
            Roles = new FakeRoleRepository(roles);
            Operators = new FakeOperatorRepositoryWithSaves(operators);

            _createRole = new CreateRoleHandler(Roles, Permissions, new SequentialIdGenerator(), new FakeClock(Now));
            _listRoles = new ListRolesForTenantHandler(Roles, Permissions);
            _listOperators = new ListOperatorsForTenantHandler(Operators, Permissions);
            _grant = new GrantOperatorRoleHandler(Operators, Roles, Permissions);
            _revoke = new RevokeOperatorRoleHandler(Operators, Roles, Permissions);
        }

        public FakeRoleRepository Roles { get; }

        public FakeOperatorRepositoryWithSaves Operators { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public Task<Ago.Platform.Kernel.Result<RoleId>> CreateRoleAsync(
            string name, IReadOnlyList<Permission> permissions) =>
            _createRole.HandleAsync(new CreateRole(Caller, TenantId, name, permissions), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<Role>>> ListRolesAsync() =>
            _listRoles.HandleAsync(new ListRolesForTenant(Caller, TenantId), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<Operator>>> ListOperatorsAsync() =>
            _listOperators.HandleAsync(new ListOperatorsForTenant(Caller, TenantId), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result> GrantAsync(OperatorId targetOperatorId, RoleId roleId) =>
            _grant.HandleAsync(
                new GrantOperatorRole(Caller, TenantId, targetOperatorId, roleId), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result> RevokeAsync(OperatorId targetOperatorId, RoleId roleId) =>
            _revoke.HandleAsync(
                new RevokeOperatorRole(Caller, TenantId, targetOperatorId, roleId), CancellationToken.None);
    }
}
