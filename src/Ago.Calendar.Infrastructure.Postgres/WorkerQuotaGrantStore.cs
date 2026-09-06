using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`22-07`'s <see cref="IWorkerQuotaGrantStore"/> adapter - see that interface's own remarks
/// for why this manages and commits its own transaction rather than staging for a caller to combine
/// with an inbox record.</summary>
public sealed class WorkerQuotaGrantStore(AgoCalendarDbContext db) : IWorkerQuotaGrantStore
{
    public async Task ApplyAsync(TenantId tenantId, int quota, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quota);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Locked first, before either the worker read below or the quota write at the end - the same
        // "the lock has to be taken this early" reasoning WorkerRepository.TryAddWithinQuotaAsync
        // gives for its own identical lock on this row. A concurrent worker-creation attempt for this
        // tenant blocks on this exact statement until this transaction commits, and then re-reads the
        // quota this call is about to write - closing the race between "grant lowers N" and "a new
        // worker is created under the old N" without either side needing to know the other exists.
        await LockTenantAsync(tenantId, cancellationToken);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            // The same "a foreign key should have prevented this" judgement
            // WorkerRepository.LockTenantAndReadWorkerQuotaAsync makes - this consumer only ever
            // resolves TenantId from ago-chat's own SiteId (22-03: the calendar's tenancy row equals
            // the account id), and every tenant this product's own module registration ever creates
            // writes that row before anything downstream could reference it.
            throw new InvalidOperationException(
                $"Tenant {tenantId.Value} was not found while applying a worker quota grant.");
        }

        var activeWorkers = await db.Workers
            .Where(w => w.TenantId == tenantId && w.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var worker in WorkerQuotaPolicy.SelectWorkersToDeactivate(activeWorkers, quota))
        {
            worker.Deactivate(now);
        }

        tenant.GrantWorkerQuota(quota);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Lock only - no value is needed back, unlike
    /// <c>WorkerRepository.LockTenantAndReadWorkerQuotaAsync</c>'s scalar read, because this method is
    /// about to overwrite <see cref="Tenant.WorkerQuota"/> outright rather than compare against
    /// it.</summary>
    private async Task LockTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id = @tenantId FOR UPDATE", connection, pgTransaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
