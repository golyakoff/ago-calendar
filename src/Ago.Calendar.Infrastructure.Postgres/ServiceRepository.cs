using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class ServiceRepository(AgoCalendarDbContext db) : IServiceRepository
{
    public Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken) =>
        db.Services.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Service>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await db.Services
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Service service, CancellationToken cancellationToken)
    {
        db.Services.Add(service);
        await db.SaveChangesAsync(cancellationToken);
    }
}
