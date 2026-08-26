using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class TenantRepository(AgoCalendarDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken) =>
        db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TenantId>> ListIdsAsync(
        TenantId? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // SQL rather than LINQ, for a reason that is specific to strongly-typed ids: `TenantId` is a
        // record struct with a value converter, so it has no `>` operator for EF to translate and a
        // keyset predicate cannot be written in LINQ at all without unwrapping the id inside the
        // expression tree - which the converter does not let the provider see through. Unwrapping in
        // SQL, where the column is a plain uuid, is the honest version.
        //
        // Keyset, not OFFSET (data-model.md): the last page costs what the first one does, and rows
        // created while the job is walking cannot make it skip a tenant.
        var sql = after is null
            ? "SELECT id FROM tenants ORDER BY id LIMIT @limit"
            : "SELECT id FROM tenants WHERE id > @after ORDER BY id LIMIT @limit";

        var parameters = after is null
            ? new NpgsqlParameter[] { new("limit", limit) }
            : [new NpgsqlParameter("limit", limit), new NpgsqlParameter("after", after.Value.Value)];

        var ids = await db.Database.SqlQueryRaw<Guid>(sql, parameters).ToListAsync(cancellationToken);
        return ids.ConvertAll(id => new TenantId(id));
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }
}
