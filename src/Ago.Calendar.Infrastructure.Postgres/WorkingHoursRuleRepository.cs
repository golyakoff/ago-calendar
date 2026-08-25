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

    public async Task AddAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        db.WorkingHoursRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
    }
}
