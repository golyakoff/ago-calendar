using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.TenantErasure;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.TenantErasure;

/// <summary>`22-30`, at the Application level - the handler is a thin pass-through, so what this
/// suite actually proves is idempotency: the second call, against a tenant the first call already
/// removed, must land on the identical "confirmed clean" answer rather than fail or behave
/// differently.</summary>
public sealed class EraseTenantDataHandlerTests
{
    private static readonly TenantId TenantId = new(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_ForATenantThatExists_ErasesItAndConfirms()
    {
        var erasure = new FakeTenantErasureRepository();
        erasure.Seed(TenantId);
        var handler = new EraseTenantDataHandler(erasure);

        var result = await handler.HandleAsync(new EraseTenantData(TenantId), CancellationToken.None);

        Assert.True(result.TenantExisted);
        Assert.True(result.Confirmed);
        Assert.False(erasure.Exists(TenantId));
    }

    /// <summary>`CLAUDE.md` rule 5: at-least-once delivery is assumed everywhere, including a call
    /// over plain HTTP with no idempotency key of its own - a retried erase (or a second one issued
    /// because the caller never saw the first response) must erase once and never fail the second
    /// time.</summary>
    [Fact]
    public async Task HandleAsync_CalledTwice_IsANoOpTheSecondTime_AndStaysConfirmed()
    {
        var erasure = new FakeTenantErasureRepository();
        erasure.Seed(TenantId);
        var handler = new EraseTenantDataHandler(erasure);

        var first = await handler.HandleAsync(new EraseTenantData(TenantId), CancellationToken.None);
        var second = await handler.HandleAsync(new EraseTenantData(TenantId), CancellationToken.None);

        Assert.True(first.TenantExisted);
        Assert.False(second.TenantExisted);
        Assert.True(first.Confirmed);
        Assert.True(second.Confirmed);
    }

    [Fact]
    public async Task HandleAsync_ForATenantThatNeverExisted_IsConfirmedCleanWithoutError()
    {
        var erasure = new FakeTenantErasureRepository();
        var handler = new EraseTenantDataHandler(erasure);

        var result = await handler.HandleAsync(new EraseTenantData(TenantId), CancellationToken.None);

        Assert.False(result.TenantExisted);
        Assert.True(result.Confirmed);
    }

    /// <summary>Plain in-memory fake, the same "hand-written, not a mocking framework" shape every
    /// other fake in this project follows (testing.md) - mirrors what a real Postgres re-read would
    /// answer: a row is either there or it is not, and <see cref="TenantErasureResult.Confirmed"/> is
    /// always true once this returns.</summary>
    private sealed class FakeTenantErasureRepository : ITenantErasureRepository
    {
        private readonly HashSet<TenantId> _tenants = [];

        public void Seed(TenantId tenantId) => _tenants.Add(tenantId);

        public bool Exists(TenantId tenantId) => _tenants.Contains(tenantId);

        public Task<TenantErasureResult> EraseAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            var existed = _tenants.Remove(tenantId);
            return Task.FromResult(new TenantErasureResult(existed, Confirmed: true));
        }
    }
}
