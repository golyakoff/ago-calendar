using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class WorkerRepository(AgoCalendarDbContext db) : IWorkerRepository
{
    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken) =>
        LoadedWorkers().FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken)
    {
        // The membership rows are queried directly rather than through the aggregate's own private
        // collection: filtering on a field-backed navigation is expressible but reads as reflection,
        // and the join table is a first-class mapped entity here precisely so a query like this can
        // say what it means.
        var members = db.Set<CalendarMembership>()
            .Where(membership => membership.CalendarId == calendarId)
            .Select(membership => membership.WorkerId);

        return await LoadedWorkers()
            .Where(worker => worker.IsActive && members.Contains(worker.Id))
            .OrderBy(worker => worker.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Worker>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await LoadedWorkers()
            .Where(worker => worker.TenantId == tenantId)
            .OrderBy(worker => worker.DisplayName)
            .ToListAsync(cancellationToken);

    /// <summary>See <see cref="IWorkerRepository.TryAddWithinQuotaAsync"/> for the reasoning behind
    /// a lock-and-count rather than a denormalized counter. This method opens and commits (or rolls
    /// back) its own transaction - <see cref="CreateWorkerHandler"/> has no ambient one of its own to
    /// join, and refusing here must leave nothing written, matching
    /// <c>OperatorInviteRedemptionRepository.RedeemAsync</c>'s identical shape one repository
    /// over.</summary>
    public async Task<bool> TryAddWithinQuotaAsync(Worker worker, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var quota = await LockTenantAndReadWorkerQuotaAsync(worker.TenantId, cancellationToken);
        var activeWorkerCount = await db.Workers.CountAsync(
            w => w.TenantId == worker.TenantId && w.IsActive, cancellationToken);

        if (activeWorkerCount >= quota)
        {
            // Rolled back, nothing committed - the identical "a capacity-rejected attempt leaves
            // nothing behind" guarantee OperatorInviteRedemptionRepository's own seat-limit check
            // makes, restated for this product's own quota.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        db.Workers.Add(worker);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Raw SQL through this <see cref="AgoCalendarDbContext"/>'s own open connection/
    /// transaction, the same reason <c>OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync</c>
    /// gives for its own identical shape: the lock only means anything if the count read and the
    /// eventual insert happen on the same Postgres connection and transaction as the lock itself, and
    /// EF has no LINQ shape for <c>FOR UPDATE</c> on a scalar read.</summary>
    private async Task<int> LockTenantAndReadWorkerQuotaAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var pgTransaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            "SELECT worker_quota FROM tenants WHERE id = @tenantId FOR UPDATE", connection, pgTransaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            // A foreign key (WorkerConfiguration.HasOne<Tenant>) should make this unreachable - a
            // worker cannot be created for a tenant row that does not exist, and this product has no
            // tenant-deletion path at all. The identical reasoning
            // OperatorInviteRedemptionRepository's own missing-site case gives for throwing rather
            // than returning a case a caller has legal recourse for.
            throw new InvalidOperationException(
                $"Tenant {tenantId.Value} was not found while creating a worker - a foreign key should have prevented this.");
        }

        return (int)result;
    }

    public async Task SaveAsync(Worker worker, CancellationToken cancellationToken)
    {
        if (db.Entry(worker).State == EntityState.Detached)
        {
            db.Workers.Add(worker);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One statement, and the statement is the whole guarantee - see <see cref="IWorkerRepository"/>'s
    /// own remarks on why a preceding read would leave a gap a concurrent booking could land in. Raw
    /// SQL rather than EF's <c>ExecuteDeleteAsync</c>, because the safety condition is a correlated
    /// <c>NOT EXISTS</c> against a different table and EF Core's query translator has no LINQ shape
    /// for "delete this row unless a row in another table says not to" - the same reason
    /// <see cref="EventRepository.InsertAvailableSlotsAsync"/> drops to raw SQL for its own
    /// single-statement guarantee.
    ///
    /// <para>Deleting <c>workers</c> directly, relying on the schema's own <c>ON DELETE CASCADE</c>
    /// for <c>calendar_workers</c>, <c>worker_services</c>, <c>working_hours_rules</c> and
    /// <c>events</c>, is what makes "his slots, rules and joins go with him" true in the same
    /// statement rather than as four separate deletes this method would otherwise have to orchestrate
    /// and could partially fail across.</para>
    /// </summary>
    public async Task<bool> DeleteIfNeverBookedAsync(WorkerId id, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql =
            """
            DELETE FROM workers w
            WHERE w.id = @id
              AND w.tenant_id = @tenant_id
              AND NOT EXISTS (
                  SELECT 1 FROM events e
                  WHERE e.worker_id = w.id
                    AND e.status IN ('PendingConfirmation', 'Booked', 'NoShow')
              )
            """;

        var affected = await db.Database.ExecuteSqlRawAsync(
            sql,
            [new NpgsqlParameter("id", id.Value), new NpgsqlParameter("tenant_id", tenantId.Value)],
            cancellationToken);

        return affected > 0;
    }

    /// <summary>Both joins, always. See <see cref="IWorkerRepository"/> for why a partially loaded
    /// worker is worse than no worker: <c>WorksIn</c> and <c>Offers</c> would answer confidently and
    /// wrongly.</summary>
    private IQueryable<Worker> LoadedWorkers() =>
        db.Workers.Include("_calendars").Include("_services");
}
