using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-30`: relies on `Stage20CreateCalendarSchema`'s own foreign keys for everything under a
/// <see cref="Tenant"/> that has one - <see cref="ITenantErasureRepository"/>'s own remarks explain
/// why this needs no bounded-batch shape the way `Ago.Chat.Worker.SiteErasureJob` does.
///
/// <para><b>Two explicit deletes this cascade does not reach, found while proving this item's own
/// first Done-when against a real Postgres rather than assumed from the schema.</b>
/// `role_assignment_projections` and `contact_visibility_projections` are both replicated from AGO
/// Chat's own outbox (`22-05`/`23-12`, `adr/0093`) and both carry a bare <c>tenant_id</c> column with
/// **no foreign key to <c>tenants</c> at all** - <c>RoleAssignmentProjectionConfiguration</c>/
/// <c>ContactVisibilityProjectionConfiguration</c> neither one calls <c>HasOne&lt;Tenant&gt;()</c>,
/// deliberately: a snapshot projection fed by an idempotent consumer is not an owned child of the
/// aggregate it happens to be keyed by, the same reasoning that keeps every other outbox-fed
/// projection in this codebase FK-free. That is the right call for what those tables are *for*, and
/// it is exactly the kind of gap this item's own title warns about - "the second half is proved
/// rather than assumed": the personal-data map's own README calls "operators" a cascaded table, and
/// after `22-05`'s migration that table is <c>role_assignment_projections</c>, which does not cascade
/// at all. A tenant erasure that only issued <c>DELETE FROM tenants</c> would have left every
/// operator's Keycloak subject id (<see cref="RoleAssignmentProjectionRecord.ExternalSubjectId"/>) -
/// personal data about a person, not about the tenant - behind forever.</para>
/// </summary>
public sealed class TenantErasureRepository(AgoCalendarDbContext db) : ITenantErasureRepository
{
    public async Task<TenantErasureResult> EraseAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var existed = tenant is not null;

        // One transaction for all three statements - not load-bearing for correctness (every
        // statement here is independently idempotent, so a crash between them just leaves something
        // for the next retry to finish, ITenantErasureRepository's own remarks on why this port must
        // be idempotent), but cheap, and it means Confirmed below is never read against a half-applied
        // erasure this same call made.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The two projection tables no foreign key reaches - see this class's own remarks. Bulk
        // deletes, not a load-then-remove: neither table's rows need to pass through the change
        // tracker for this operation, and a tenant with many operators should not cost one round trip
        // per row.
        await db.Set<RoleAssignmentProjectionRecord>()
            .Where(r => r.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Set<ContactVisibilityProjectionRecord>()
            .Where(r => r.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        if (tenant is not null)
        {
            // Every other row this product holds for the tenant cascades from this foreign key
            // already (ITenantErasureRepository's own remarks list them).
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // `adr/0149` rule 2: the proof is a fresh read, not trust that the statements above did not
        // throw. Raw SQL rather than `db.Tenants.AnyAsync(...)` on purpose - the just-deleted entity
        // is still in this context's own change tracker, and a LINQ query against a DbSet can be
        // satisfied out of the tracker's local state for a tracked-and-removed entity without ever
        // touching Postgres for some provider/query-shape combinations. `SqlQueryRaw` bypasses the
        // tracker entirely, the identical reason `TenantRepository.AnyAllowsOriginAsync` already uses
        // it for its own existence check. All three tables, not only `tenants` - this class's own
        // remarks are the reason a single-table check would have called this item done while it was
        // not.
        var stillExists = await db.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM tenants WHERE id = @id
                    UNION ALL SELECT 1 FROM role_assignment_projections WHERE tenant_id = @id
                    UNION ALL SELECT 1 FROM contact_visibility_projections WHERE tenant_id = @id
                ) AS "Value"
                """,
                new NpgsqlParameter("id", tenantId.Value))
            .SingleAsync(cancellationToken);

        return new TenantErasureResult(existed, Confirmed: !stillExists);
    }
}
