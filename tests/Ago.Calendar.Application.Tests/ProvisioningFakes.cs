using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`22-05`/`adr/0093`: <c>RegisterTenantHandlerTests</c>'s own assertion surface -
/// <c>ITenantRepository</c>'s own <c>AddAsync</c>, faked, records what it was asked (testing.md:
/// hand-written, not a mocking framework). the removed FakeTenantProvisioningStore - the
/// three-aggregate fake this replaces - is gone along with <c>ITenantProvisioningStore</c>: provisioning
/// a tenant is a single-aggregate write now.</summary>
internal sealed class RecordingTenantRepository : ITenantRepository
{
    public Tenant? Registered { get; private set; }

    public Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken) =>
        Task.FromResult(Registered?.Id == id ? Registered : null);

    public Task<IReadOnlyList<TenantId>> ListIdsAsync(TenantId? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TenantId>>([]);

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        Registered = tenant;
        return Task.CompletedTask;
    }

    public Task<Tenant?> FindByPublicKeyAsync(TenantPublicKey publicKey, CancellationToken cancellationToken) =>
        Task.FromResult(Registered?.PublicKey == publicKey ? Registered : null);

    public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task SaveAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        Registered = tenant;
        return Task.CompletedTask;
    }
}
