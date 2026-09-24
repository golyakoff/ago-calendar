using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class WorkingHoursRuleRepository(AgoCalendarDbContext db) : IWorkingHoursRuleRepository
{
    public async Task<IReadOnlyList<WorkingHoursRule>> ListForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        await db.WorkingHoursRules
            .Where(rule => rule.CalendarId == calendarId)
            .OrderBy(rule => rule.WorkerId)
            .ThenBy(rule => rule.DayOfWeek)
            .ThenBy(rule => rule.StartsAt)
            .ToListAsync(cancellationToken);

    public async Task<WorkingHoursRule?> GetByIdAsync(WorkingHoursRuleId id, CancellationToken cancellationToken) =>
        await db.WorkingHoursRules.FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);

    public async Task AddAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        db.WorkingHoursRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        // Update() rather than a bare SaveChangesAsync on the tracked instance: in production the
        // rule always arrives tracked (the handler loaded it through GetByIdAsync on this same scoped
        // context) and this call is a no-op, but a caller holding a detached instance would otherwise
        // save nothing at all and look like it had succeeded.
        db.WorkingHoursRules.Update(rule);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        db.WorkingHoursRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
    }
}
