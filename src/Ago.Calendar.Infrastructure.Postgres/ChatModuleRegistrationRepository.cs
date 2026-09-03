using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>The adapter for <see cref="IChatModuleRegistrationRepository"/> - a plain point lookup and
/// insert, the same shape <c>TenantRepository.GetByIdAsync</c> uses for its own single-key row.</summary>
public sealed class ChatModuleRegistrationRepository(AgoCalendarDbContext db) : IChatModuleRegistrationRepository
{
    public Task<ChatModuleRegistration?> GetByTenantIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        db.ChatModuleRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);

    public async Task AddAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
    {
        db.ChatModuleRegistrations.Add(registration);
        await db.SaveChangesAsync(cancellationToken);
    }
}
