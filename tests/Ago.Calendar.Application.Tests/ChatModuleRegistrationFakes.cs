using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>Plain in-memory store for <see cref="ChatModuleRegistration"/> - the same
/// "hand-written, not a mocking framework" shape every other fake in this project follows
/// (testing.md). Keyed by <see cref="TenantId"/>, the row's own primary key.</summary>
internal sealed class FakeChatModuleRegistrationRepository : IChatModuleRegistrationRepository
{
    private readonly Dictionary<TenantId, ChatModuleRegistration> _rows = [];

    public Task<ChatModuleRegistration?> GetByTenantIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.GetValueOrDefault(tenantId));

    public Task AddAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
    {
        _rows[registration.TenantId] = registration;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
    {
        _rows[registration.TenantId] = registration;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        _rows.Remove(tenantId);
        return Task.CompletedTask;
    }
}
