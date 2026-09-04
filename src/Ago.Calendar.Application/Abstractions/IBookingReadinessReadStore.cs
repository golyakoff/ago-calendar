using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// adr/0004's read side for `23-23`'s own question: not "is this tenant bookable" as a boolean, but
/// which of the several things that must be true before a visitor can book is not yet true - backs
/// <c>GetBookingReadinessHandler</c>.
///
/// <para><b>A read store, not a repository or a walk through five aggregates.</b> Every fact here
/// mirrors a real refusal point already on the booking path - <see cref="IBookingSurfaceReadStore"/>'s
/// own <c>ServicesSql</c>/<c>WorkersSql</c> filter through exactly the same joins - but this port
/// answers a different question than that one does: not "what can a visitor pick right now" (which
/// silently omits whatever is missing) but "what, named, is missing". Duplicating the joins here
/// rather than reusing <see cref="IBookingSurfaceReadStore"/> is deliberate: that port's rows are
/// shaped for a customer choosing a service, and coercing "nothing came back" into six distinct named
/// reasons would push a screen-shaping decision into a port two very different callers share - the
/// same "a read model shaped for one screen is not a repository other screens should also reach
/// through" reasoning `adr/0004` already states.</para>
///
/// <para><b>One row per calendar the tenant has created - zero rows for a tenant with none.</b> That
/// is a real, first-run state; <c>GetBookingReadinessHandler</c> turns it into the fully-unmet answer
/// Done-when's first clause asks for, rather than this port inventing a placeholder row nobody asked
/// it for. Shaping "no calendar at all" into a reportable answer is a screen-facing decision that
/// belongs one layer up, not a fact this query can independently discover from empty tables.</para>
///
/// <para><b>Each fact is a funnel over the calendar's own active workers, not an unscoped "does one
/// exist anywhere".</b> "A service must exist and be one the worker performs" names a worker, not two
/// unrelated existence checks - so <see cref="CalendarReadinessRow.HasWorkerWithService"/> asks
/// whether an active worker on <i>this</i> calendar offers a service, and each later fact narrows the
/// same survivor set further: a worker who cleared the service check and then also has hours, then
/// also has a schedule. A tenant with one idle worker and one fully-configured worker reads as ready
/// on every fact past "a worker exists" - "someone bookable exists" is the whole question `flows.md`
/// 3.1 asks, not "is everyone finished".</para>
///
/// <para><b><see cref="CalendarReadinessRow.HasWorkingHours"/> treats a <see cref="ScheduleKind.Cycle"/>
/// schedule as hours already configured.</b> `adr/0084`'s own point: a cycle schedule carries its own
/// wall-clock window (<c>CycleStartsAt</c>/<c>CycleEndsAt</c>) and never reads
/// <c>working_hours_rules</c> at all - <c>MaterializeAvailabilityHandler</c> only consults that table
/// for a <see cref="ScheduleKind.Weekly"/> schedule. Requiring a <c>working_hours_rules</c> row for a
/// cycle-scheduled worker would report a real, materialising setup as broken, which is exactly the
/// failure this item exists to stop happening in the other direction.</para>
/// </summary>
public interface IBookingReadinessReadStore
{
    /// <param name="now">Only for <see cref="CalendarReadinessRow.HasFutureSlots"/> - the same
    /// <c>starts_at &gt; @NotBefore</c> predicate <see cref="IBookingSurfaceReadStore.ListOpenSlotsAsync"/>
    /// reads through, so "produced slots inside the horizon" means the identical thing here that it
    /// means to a customer looking for one.</param>
    Task<IReadOnlyList<CalendarReadinessRow>> GetForTenantAsync(
        TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <param name="HasWorker">An active worker joined to this calendar (<c>calendar_workers</c>, filtered
/// to <c>workers.is_active</c>) - regardless of what that worker offers or has configured.</param>
/// <param name="HasWorkerWithService">Among <see cref="HasWorker"/>'s survivors, at least one who
/// offers a <c>service</c> that still exists.</param>
/// <param name="HasWorkingHours">Among <see cref="HasWorkerWithService"/>'s survivors, at least one
/// with a working-hours rule on this calendar, or a <see cref="ScheduleKind.Cycle"/> schedule - see
/// this type's own remarks on why a cycle schedule satisfies this fact on its own.</param>
/// <param name="HasSchedule">Among those, at least one with a <c>worker_schedules</c> row -
/// <c>MaterializeAvailabilityHandler</c>'s own gate: no schedule, nothing bookable, regardless of
/// hours.</param>
/// <param name="HasFutureSlots">This calendar's own <c>events</c> rows: at least one
/// <c>Available</c> row starting after the read's own <c>now</c>. Calendar-wide rather than chained
/// off the same worker as the facts above - materialisation is what it is, however it got there, and
/// this is the one fact `flows.md` 3.1 names by itself: "a setup that looks finished and produces no
/// slots".</param>
public readonly record struct CalendarReadinessRow(
    CalendarId CalendarId,
    string CalendarName,
    bool IsPublished,
    bool HasWorker,
    bool HasWorkerWithService,
    bool HasWorkingHours,
    bool HasSchedule,
    bool HasFutureSlots);
