using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="WorkerSchedule"/>. `20-14`'s own aggregate boundary decision -
/// a schedule is a worker's own row, not a list inside <see cref="Worker"/> - is what makes this port
/// separate from <see cref="IWorkerRepository"/> rather than a widened method on it: the two have
/// different callers (a console CRUD screen versus a background job) and different write patterns
/// (a human's occasional save versus the job's own cursor advance on every run), and a port shaped by
/// its use case keeps those from becoming one bloated interface neither caller can reason about.
/// </summary>
public interface IWorkerScheduleRepository
{
    /// <summary>At most one schedule per worker - `20-14`'s "one worker, one schedule" shape.
    /// <see langword="null"/> is a real, common state: a worker with no schedule yet materialises
    /// nothing, the same way a worker with no <see cref="WorkingHoursRule"/> did before this item.</summary>
    Task<WorkerSchedule?> GetByWorkerIdAsync(WorkerId workerId, CancellationToken cancellationToken);

    /// <summary>Every schedule of the workers on one calendar, in the shape
    /// <c>MaterializeAvailabilityHandler</c> consumes it - one query per materialisation run rather
    /// than one per worker, the same batching <see cref="IWorkingHoursRuleRepository.ListForCalendarAsync"/>
    /// already does for the weekly rules.</summary>
    Task<IReadOnlyList<WorkerSchedule>> ListForCalendarAsync(CalendarId calendarId, CancellationToken cancellationToken);

    Task AddAsync(WorkerSchedule schedule, CancellationToken cancellationToken);

    Task SaveAsync(WorkerSchedule schedule, CancellationToken cancellationToken);
}
