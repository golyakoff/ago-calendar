namespace Ago.Calendar.Contracts;

/// <summary>
/// `26-315`: a worker's schedule was created or reconfigured - `Ago.Calendar.Worker`'s own trigger to
/// materialise that calendar right away, rather than only waiting for
/// <c>AvailabilityMaterializationJob</c>'s own daily tick. This is this item's preferred fix for its own
/// root cause: a schedule saved after the last daily tick previously produced zero materialised slots
/// for up to 24h, with no operator action able to shorten that. See
/// <c>Ago.Calendar.Worker.WorkerScheduleSavedMaterializationConsumer</c> for the reaction, and
/// <c>Ago.Calendar.Application.UseCases.RecutSchedule.RecutConfirmHandler</c>'s own bootstrap branch for
/// the second, human-triggered path to the identical outcome («Пересчёт» on a worker with nothing
/// materialised yet).
/// </summary>
/// <param name="CalendarId">The calendar to materialise - <c>MaterializeAvailabilityHandler</c>'s own
/// unit of work, resolved from the worker's own calendar membership at save time
/// (<c>SaveWorkerScheduleHandler</c>'s own remarks: never published for a worker joined to no calendar
/// yet, since there is nothing for the consumer to materialise into).</param>
/// <param name="WorkerId">A hint for a future consumer that wants to act on one specific worker; today's
/// only consumer materialises the whole calendar - the identical granularity the daily job itself
/// uses, since there is no narrower port to call instead.</param>
/// <param name="OccurredAt">When the schedule was saved.</param>
/// <param name="CorrelationId">A fresh id, minted at the save - this event can fire more than once for
/// the same worker (every reconfigure re-publishes it), so it cannot reuse one correlation id across
/// saves without implying a causal link between two unrelated operator actions - the identical reasoning
/// <c>BookingPendingStateChanged</c>'s own remarks give for its own fresh id.</param>
public sealed record WorkerScheduleSaved(
    Guid CalendarId, Guid WorkerId, DateTimeOffset OccurredAt, Guid CorrelationId);
