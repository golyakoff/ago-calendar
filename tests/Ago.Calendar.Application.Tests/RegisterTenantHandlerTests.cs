using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `22-05`/`adr/0093`: rewritten for the simplified handler - a tenant, nothing riding along with it.
/// The account-owner-identity coverage this file used to carry (<c>ExternalSubjectId</c> vs
/// <c>OwnerEmail</c>, the account-owner invariant, adr/0088's invite shape) is gone along with the
/// code it tested; what remains is `22-03`'s own tenant-id provenance tests, unchanged in substance.
/// </summary>
public class RegisterTenantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Register_WritesExactlyOneTenant_NothingElse()
    {
        var world = new World();

        var result = await world.RegisterAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(world.Store.Registered);
        Assert.Equal(result.Value.TenantId, world.Store.Registered!.Id);
        Assert.Equal("Barbershop", world.Store.Registered.Name);
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

        var result = await world.RegisterAsync(tenantId: accountId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(accountId, result.Value.TenantId);
        Assert.Equal(accountId, world.Store.Registered!.Id);
    }

    /// <summary>
    /// `22-03`'s other half of "equals, not replaced by": with nothing supplied, this handler still
    /// mints its own id - the standalone door adr/0093 keeps open, proven here rather than only by
    /// the earlier test never happening to pass one.
    /// </summary>
    [Fact]
    public async Task RegisterWithNoTenantIdSupplied_TheHandlerStillMintsOne()
    {
        var world = new World();

        var result = await world.RegisterAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEqual(default, result.Value.TenantId.Value);
        Assert.Equal(result.Value.TenantId, world.Store.Registered!.Id);
    }

    [Fact]
    public async Task RegisterWithAMalformedPublicKey_IsRejectedBeforeTouchingTheStore()
    {
        var world = new World();

        var result = await world.RegisterAsync(publicKey: string.Empty);

        Assert.Equal("provisioning.invalid", result.Error!.Value.Code);
        Assert.Null(world.Store.Registered);
    }

    private sealed class World
    {
        private readonly RegisterTenantHandler _handler;

        public World()
        {
            Store = new RecordingTenantRepository();
            _handler = new RegisterTenantHandler(Store, new SequentialIdGenerator(), new FakeClock(Now));
        }

        public RecordingTenantRepository Store { get; }

        public Task<Ago.Platform.Kernel.Result<RegisteredTenant>> RegisterAsync(
            string? publicKey = null, TenantId? tenantId = null) =>
            _handler.HandleAsync(
                new RegisterTenant(
                    "Barbershop", publicKey ?? $"shop-{Guid.NewGuid():N}"[..24], [], tenantId),
                CancellationToken.None);
    }
}
