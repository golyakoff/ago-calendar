using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

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

    public async Task AddAsync(Worker worker, CancellationToken cancellationToken)
    {
        db.Workers.Add(worker);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(Worker worker, CancellationToken cancellationToken)
    {
        if (db.Entry(worker).State == EntityState.Detached)
        {
            db.Workers.Add(worker);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Both joins, always. See <see cref="IWorkerRepository"/> for why a partially loaded
    /// worker is worse than no worker: <c>WorksIn</c> and <c>Offers</c> would answer confidently and
    /// wrongly.</summary>
    private IQueryable<Worker> LoadedWorkers() =>
        db.Workers.Include("_calendars").Include("_services");
}
