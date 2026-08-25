using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class TenantRepository(AgoCalendarDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken) =>
        db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }
}
