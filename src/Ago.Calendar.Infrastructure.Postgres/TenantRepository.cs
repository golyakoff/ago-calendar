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

    public Task<Tenant?> FindByPublicKeyAsync(TenantPublicKey publicKey, CancellationToken cancellationToken) =>
        db.Tenants.FirstOrDefaultAsync(t => t.PublicKey == publicKey, cancellationToken);

    public async Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        // Raw SQL for the same reason ListIdsAsync above is raw: `allowed_origins` sits behind a
        // value converter, so EF cannot see through it to translate array containment - the LINQ
        // form fails at runtime with "could not be translated". In SQL the column is a plain text[]
        // and `= ANY(...)` is one index probe against ix_tenants_allowed_origins.
        //
        // `EXISTS`, not `COUNT`: the question is whether one row exists, and Postgres can stop at the
        // first.
        var allowed = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM tenants WHERE @origin = ANY(allowed_origins)) AS \"Value\"",
                new NpgsqlParameter("origin", origin))
            .SingleAsync(cancellationToken);

        return allowed;
    }

    public async Task SaveAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }
}
