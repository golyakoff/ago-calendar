using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class WorkerScheduleRepository(AgoCalendarDbContext db) : IWorkerScheduleRepository
{
    public Task<WorkerSchedule?> GetByWorkerIdAsync(WorkerId workerId, CancellationToken cancellationToken) =>
        db.WorkerSchedules.FirstOrDefaultAsync(schedule => schedule.WorkerId == workerId, cancellationToken);

    public async Task<IReadOnlyList<WorkerSchedule>> ListForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken)
    {
        // The join table queried directly, exactly the way WorkerRepository.ListActiveForCalendarAsync
        // already does it - WorkerSchedule carries no CalendarId of its own (a schedule is the
        // worker's own row, and a worker's calendar comes from the join `20-01` already owns), so
        // "every schedule on this calendar" has to go through calendar_workers to get there.
        var memberWorkerIds = db.Set<CalendarMembership>()
            .Where(membership => membership.CalendarId == calendarId)
            .Select(membership => membership.WorkerId);

        return await db.WorkerSchedules
            .Where(schedule => memberWorkerIds.Contains(schedule.WorkerId))
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(WorkerSchedule schedule, CancellationToken cancellationToken)
    {
        db.WorkerSchedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(WorkerSchedule schedule, CancellationToken cancellationToken)
    {
        if (db.Entry(schedule).State == EntityState.Detached)
        {
            db.WorkerSchedules.Update(schedule);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
