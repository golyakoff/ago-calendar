using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`20-10`: the EF adapter for <see cref="IPendingPhoneVerificationRepository"/> - see that
/// port's own remarks for why one <see cref="SaveAsync"/> suffices for every caller.</summary>
public sealed class PendingPhoneVerificationRepository(AgoCalendarDbContext db) : IPendingPhoneVerificationRepository
{
    public Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken) =>
        db.PendingPhoneVerifications.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken)
    {
        if (db.Entry(verification).State == EntityState.Detached)
        {
            db.PendingPhoneVerifications.Add(verification);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
