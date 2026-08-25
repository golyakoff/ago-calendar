using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="WorkingHoursRule"/>. One read method, scoped to a calendar,
/// because that is exactly the unit `20-02` materialises in: a calendar's zone plus every rule
/// inside it is the complete input to "produce the next N days of slots".
/// </summary>
public interface IWorkingHoursRuleRepository
{
    Task<IReadOnlyList<WorkingHoursRule>> ListForCalendarAsync(CalendarId calendarId, CancellationToken cancellationToken);

    Task AddAsync(WorkingHoursRule rule, CancellationToken cancellationToken);
}
