using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `ago-root#363`: until now nothing at the Application level exercised
/// <see cref="RegisterTenantHandler"/> at all - the only caller was <c>DevProvisioningEndpoints</c>,
/// tested only through <c>DevEndpointsEnvironmentGateTests</c>'s route-presence assertion, which never
/// runs the handler. These tests are new coverage for existing behaviour (the <c>ExternalSubjectId</c>
/// path) as well as for the new one (<see cref="RegisterTenant.OwnerEmail"/>).
/// </summary>
public class RegisterTenantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RegisterWithExternalSubjectId_CreatesAResolvableAccountOwner()
    {
        var world = new World();

        var result = await world.RegisterAsync(externalSubjectId: "kc-owner-sub");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var (tenant, role, @operator) = world.Store.Registered!.Value;
        Assert.Equal(result.Value.TenantId, tenant.Id);
        Assert.Equal(result.Value.OperatorId, @operator.Id);
        Assert.True(@operator.IsAccountOwner);
        Assert.Equal("kc-owner-sub", @operator.ExternalSubjectId);
        Assert.Null(@operator.InvitedEmail);
        Assert.Contains(@operator.Roles, a => a.RoleId == role.Id);
    }

    [Fact]
    public async Task RegisterWithOwnerEmail_CreatesAnInvitedUnlinkedAccountOwner()
    {
        // `adr/0088`'s own mechanism, reused a caller earlier: this is the shape a real tenant's
        // first operator now takes, since a real owner's Keycloak sub cannot be known in advance.
        var world = new World();

        var result = await world.RegisterAsync(ownerEmail: "owner@example.com");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var (_, role, @operator) = world.Store.Registered!.Value;
        Assert.True(@operator.IsAccountOwner);
        Assert.Null(@operator.ExternalSubjectId);
        Assert.Equal("owner@example.com", @operator.InvitedEmail!.Value.Value);
        Assert.Contains(@operator.Roles, a => a.RoleId == role.Id);
    }

    [Fact]
    public async Task RegisterWithNeitherIdentifier_IsRejectedBeforeTouchingTheStore()
    {
        var world = new World();

        var result = await world.RegisterAsync();

        Assert.Equal("provisioning.invalid", result.Error!.Value.Code);
        Assert.Null(world.Store.Registered);
    }

    [Fact]
    public async Task RegisterWithBothIdentifiers_IsRejectedBeforeTouchingTheStore()
    {
        // Ambiguous rather than "one wins silently" - a caller passing both almost certainly made a
        // mistake, and Operator.Create itself would not have refused (its own remarks say so).
        var world = new World();

        var result = await world.RegisterAsync(externalSubjectId: "kc-owner-sub", ownerEmail: "owner@example.com");

        Assert.Equal("provisioning.invalid", result.Error!.Value.Code);
        Assert.Null(world.Store.Registered);
    }

    [Fact]
    public async Task RegisterWithAMalformedOwnerEmail_IsRejectedBeforeTouchingTheStore()
    {
        var world = new World();

        var result = await world.RegisterAsync(ownerEmail: "not-an-email");

        Assert.Equal("provisioning.invalid", result.Error!.Value.Code);
        Assert.Null(world.Store.Registered);
    }

    /// <summary>
    /// `22-03`/adr/0093: the whole point. A caller-supplied id - the account id, in production - is
    /// used as-is rather than as a hint the handler is free to discard, and the proof is against the
    /// row the fake store actually received, not against the handler's returned value alone.
    /// </summary>
    [Fact]
    public async Task RegisterWithASuppliedTenantId_TheStoredTenantCarriesThatExactId()
    {
        var world = new World();
        var accountId = new TenantId(Guid.NewGuid());

        var result = await world.RegisterAsync(externalSubjectId: "kc-owner-sub", tenantId: accountId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(accountId, result.Value.TenantId);
        var (tenant, _, @operator) = world.Store.Registered!.Value;
        Assert.Equal(accountId, tenant.Id);
        Assert.Equal(accountId, @operator.TenantId);
    }

    /// <summary>
    /// `22-03`'s other half of "equals, not replaced by": with nothing supplied, this handler still
    /// mints its own id - the standalone door adr/0093 keeps open, proven here rather than only by
    /// the earlier tests never happening to pass one.
    /// </summary>
    [Fact]
    public async Task RegisterWithNoTenantIdSupplied_TheHandlerStillMintsOne()
    {
        var world = new World();

        var result = await world.RegisterAsync(externalSubjectId: "kc-owner-sub");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEqual(default, result.Value.TenantId.Value);
        var (tenant, _, _) = world.Store.Registered!.Value;
        Assert.Equal(result.Value.TenantId, tenant.Id);
    }

    private sealed class World
    {
        private readonly RegisterTenantHandler _handler;

        public World()
        {
            Store = new FakeTenantProvisioningStore();
            _handler = new RegisterTenantHandler(Store, new SequentialIdGenerator(), new FakeClock(Now));
        }

        public FakeTenantProvisioningStore Store { get; }

        public Task<Ago.Platform.Kernel.Result<RegisteredTenant>> RegisterAsync(
            string? externalSubjectId = null, string? ownerEmail = null, TenantId? tenantId = null) =>
            _handler.HandleAsync(
                new RegisterTenant(
                    "Barbershop",
                    $"shop-{Guid.NewGuid():N}"[..24],
                    "Owner",
                    externalSubjectId,
                    [],
                    ownerEmail,
                    tenantId),
                CancellationToken.None);
    }
}
