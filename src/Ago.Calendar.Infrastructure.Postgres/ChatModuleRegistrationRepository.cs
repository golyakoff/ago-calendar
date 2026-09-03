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

    /// <summary>`22-11`: the caller always hands back an instance built from a fresh
    /// <see cref="GetByTenantIdAsync"/> read (that method's own <c>AsNoTracking</c> means it is never
    /// already tracked), so this attaches it as modified rather than assuming EF is already watching
    /// it - the same "detached means insert, present-but-unwatched means an explicit update" split
    /// <see cref="AddAsync"/>'s own doc comment on the interface names for its sibling.</summary>
    public async Task UpdateAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
    {
        db.ChatModuleRegistrations.Update(registration);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await db.ChatModuleRegistrations.Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
    }
}
