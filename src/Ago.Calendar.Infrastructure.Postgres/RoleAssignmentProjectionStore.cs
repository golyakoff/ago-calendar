using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-05`/`adr/0093`'s Infrastructure half of <see cref="IRoleAssignmentProjectionStore"/> - EF Core
/// against this product's own <c>role_assignment_projections</c> table. See that port's own remarks
/// for why the fact it stores counts as Application state with an Infrastructure adapter, not
/// Infrastructure state: the dependency rule is what keeps this class, and only this class, aware
/// that the fact happens to live in Postgres.
/// </summary>
public sealed class RoleAssignmentProjectionStore(AgoCalendarDbContext db) : IRoleAssignmentProjectionStore
{
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, TenantId tenantId, CancellationToken cancellationToken)
    {
        var row = await db.Set<RoleAssignmentProjectionRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.OperatorId == operatorId && r.TenantId == tenantId, cancellationToken);

        // A missing row is "holds nothing here" - IRoleAssignmentProjectionStore's own remarks: no
        // fallback, ever. Never distinguished from "held nothing to begin with" - a permission check
        // has no use for that distinction, only the claims transformation's own reverse lookup does.
        return row?.Permissions ?? [];
    }

    public async Task<TenantId?> FindTenantIdAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Set<RoleAssignmentProjectionRecord>()
            .AsNoTracking()
            .Where(r => r.OperatorId == operatorId)
            .Select(r => r.TenantId)
            .ToListAsync(cancellationToken);

        // Refused, not guessed, on either zero or more than one match - IRoleAssignmentProjectionStore's
        // own remarks on why "which tenant" only has one honest answer when there is exactly one row.
        return tenantIds.Count == 1 ? tenantIds[0] : null;
    }

    public async Task StageAsync(
        OperatorId operatorId,
        TenantId tenantId,
        string externalSubjectId,
        IReadOnlyList<string> permissions,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<RoleAssignmentProjectionRecord>()
            .SingleOrDefaultAsync(r => r.OperatorId == operatorId && r.TenantId == tenantId, cancellationToken);

        if (existing is null)
        {
            db.Set<RoleAssignmentProjectionRecord>().Add(new RoleAssignmentProjectionRecord
            {
                OperatorId = operatorId,
                TenantId = tenantId,
                ExternalSubjectId = externalSubjectId,
                Permissions = [.. permissions],
                UpdatedAt = asOf,
            });

            return;
        }

        // A full replace - RoleAssignmentsChanged carries the complete current set, never a delta, so
        // there is nothing to merge (IRoleAssignmentProjectionStore's own remarks). Tracked by EF, not
        // saved here - the caller (the consumer) commits this together with its own inbox record.
        existing.ExternalSubjectId = externalSubjectId;
        existing.Permissions = [.. permissions];
        existing.UpdatedAt = asOf;
    }
}
