using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `23-12`/`adr/0123`'s Infrastructure half of <see cref="IContactVisibilityProjectionStore"/> - EF
/// Core against this product's own <c>contact_visibility_projections</c> table, the identical shape
/// <see cref="RoleAssignmentProjectionStore"/> already establishes for a different projected fact.
/// </summary>
public sealed class ContactVisibilityProjectionStore(AgoCalendarDbContext db) : IContactVisibilityProjectionStore
{
    public async Task<ContactVisibility> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var row = await db.Set<ContactVisibilityProjectionRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);

        // A missing row means Visible - IContactVisibilityProjectionStore's own remarks: no fallback
        // that invents a stricter answer than the data actually proves.
        return row is null ? ContactVisibility.Visible : Enum.Parse<ContactVisibility>(row.Rung);
    }

    public async Task StageAsync(
        TenantId tenantId, ContactVisibility rung, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var existing = await db.Set<ContactVisibilityProjectionRecord>()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);

        if (existing is null)
        {
            db.Set<ContactVisibilityProjectionRecord>().Add(new ContactVisibilityProjectionRecord
            {
                TenantId = tenantId,
                Rung = rung.ToString(),
                UpdatedAt = asOf,
            });

            return;
        }

        // A full replace - ContactVisibilityChanged carries the complete current rung, never a delta
        // (IContactVisibilityProjectionStore's own remarks). Tracked by EF, not saved here - the
        // caller (the consumer) commits this together with its own inbox record.
        existing.Rung = rung.ToString();
        existing.UpdatedAt = asOf;
    }
}
