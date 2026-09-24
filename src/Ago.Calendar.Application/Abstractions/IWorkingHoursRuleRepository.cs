using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="WorkingHoursRule"/>. The calendar-scoped read is the one
/// `20-02` needs, because that is exactly the unit it materialises in: a calendar's zone plus every
/// rule inside it is the complete input to "produce the next N days of slots".
///
/// <para><b>`26-97` added the three methods a correction needs</b> - an id-addressed read, a save
/// and a delete. There is deliberately no <c>DeleteByIdAsync(ruleId, tenantId)</c> of the guarded,
/// single-statement shape <see cref="IWorkerRepository.DeleteIfNeverBookedAsync"/> uses: that one has
/// to be one statement because the fact it guards on (<i>has this worker ever been booked</i>) is a
/// live race against the booking path. Nothing races a working-hours rule - a rule is only ever read
/// by the materialisation job, which reads a whole calendar's worth at once and would simply see one
/// fewer - so the tenant check here is an ordinary load-check-write and loses nothing by being three
/// calls rather than one.</para>
/// </summary>
public interface IWorkingHoursRuleRepository
{
    Task<IReadOnlyList<WorkingHoursRule>> ListForCalendarAsync(CalendarId calendarId, CancellationToken cancellationToken);

    /// <summary>`26-97`: one rule, for the correction screen. The rule itself carries no
    /// <see cref="TenantId"/> - it is scoped to a (worker, calendar) pair - so the caller resolves
    /// the tenant through the calendar, exactly as <c>AddWorkingHoursRuleHandler</c> already
    /// does on the way in.</summary>
    Task<WorkingHoursRule?> GetByIdAsync(WorkingHoursRuleId id, CancellationToken cancellationToken);

    Task AddAsync(WorkingHoursRule rule, CancellationToken cancellationToken);

    Task SaveAsync(WorkingHoursRule rule, CancellationToken cancellationToken);

    /// <summary>`26-97`. A hard delete, and that is not the inconsistency with
    /// <see cref="EventStatus.Cancelled"/>'s own soft delete that it looks like: a cancelled booking
    /// is the history of who cancelled on whom, while a working-hours rule is configuration with no
    /// history worth keeping - the slots it already produced are the durable record, and they are
    /// untouched by this.</summary>
    Task DeleteAsync(WorkingHoursRule rule, CancellationToken cancellationToken);
}
