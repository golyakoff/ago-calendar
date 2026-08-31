using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>Holds at most one operator and lets a test add more - <see cref="Saved"/> is the
/// assertion surface for "the grant/revoke was actually persisted", the same shape
/// <c>FakeEventRepositoryWithSaves.Saved</c> already established.</summary>
internal sealed class FakeOperatorRepositoryWithSaves(params Operator[] seeded) : IOperatorRepository
{
    private readonly List<Operator> _operators = [.. seeded];

    public List<Operator> Saved { get; } = [];

    public Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken) =>
        Task.FromResult(_operators.Find(o => o.Id == id));

    public Task<Operator?> FindByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the access-control handlers.");

    public Task<IReadOnlyList<Operator>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Operator>>([.. _operators.Where(o => o.TenantId == tenantId)]);

    public Task AddAsync(Operator @operator, CancellationToken cancellationToken)
    {
        _operators.Add(@operator);
        return Task.CompletedTask;
    }

    public Task SaveAsync(Operator @operator, CancellationToken cancellationToken)
    {
        Saved.Add(@operator);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRoleRepository(params Role[] seeded) : IRoleRepository
{
    private readonly List<Role> _roles = [.. seeded];

    public List<Role> Added { get; } = [];

    public Task<Role?> GetByIdAsync(RoleId id, CancellationToken cancellationToken) =>
        Task.FromResult(_roles.Find(r => r.Id == id));

    public Task<IReadOnlyList<Role>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Role>>([.. _roles.Where(r => r.TenantId == tenantId)]);

    public Task AddAsync(Role role, CancellationToken cancellationToken)
    {
        _roles.Add(role);
        Added.Add(role);
        return Task.CompletedTask;
    }
}

internal sealed class FakeContactsReadStore(params ContactRow[] rows) : IContactsReadStore
{
    public List<TenantId> AskedFor { get; } = [];

    public Task<IReadOnlyList<ContactRow>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        AskedFor.Add(tenantId);
        return Task.FromResult<IReadOnlyList<ContactRow>>([.. rows]);
    }
}
