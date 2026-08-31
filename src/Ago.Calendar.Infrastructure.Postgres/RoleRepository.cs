using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class RoleRepository(AgoCalendarDbContext db) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(RoleId id, CancellationToken cancellationToken) =>
        db.Roles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Role>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await db.Roles
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Role role, CancellationToken cancellationToken)
    {
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
    }
}
