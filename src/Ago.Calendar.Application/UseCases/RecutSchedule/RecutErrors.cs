using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>
/// The expected failures of `20-16`'s own two endpoints, in the <c>&lt;area&gt;.&lt;reason&gt;</c>
/// vocabulary every other use case here uses. A separate class rather than folding these into
/// <see cref="AvailabilityErrors"/>: this is a distinct destructive action with its own actor-facing
/// vocabulary (a staleness refusal, a missing per-booking decision) that manual day editing never
/// needed, the same reasoning that already gave <c>WorkerSlotsErrors</c> its own class next to
/// <see cref="AvailabilityErrors"/> rather than widening it.
/// </summary>
public static class RecutErrors
{
    public static Error Forbidden(Permission permission) => new(
        "recut.forbidden", $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error WorkerNotFound(WorkerId workerId) => new(
        "recut.worker_not_found", $"Worker {workerId.Value} does not exist in this tenant.");

    /// <summary>v1 admits exactly one calendar per worker (<see cref="Worker.JoinCalendar"/>'s own
    /// remarks) - a worker with none yet has nothing to resolve "today" or a template against.</summary>
    public static Error WorkerNotOnACalendar(WorkerId workerId) => new(
        "recut.worker_not_on_a_calendar", $"Worker {workerId.Value} is not on a calendar yet.");

    public static Error WorkerHasNoSchedule(WorkerId workerId) => new(
        "recut.worker_has_no_schedule",
        $"Worker {workerId.Value} has no schedule yet, so there is no template to re-cut from.");

    /// <summary><paramref name="from"/> has already elapsed for this calendar's own zone. Re-cutting
    /// a day that is already over would delete real history for nothing bookable to replace it -
    /// <see cref="DayGenerator.GenerateDay"/> would produce no rows for it anyway (every slot's own
    /// <c>EndsAt &lt;= now</c> filter), so the day would simply come back empty.</summary>
    public static Error FromBeforeToday(DateOnly from, DateOnly today) => new(
        "recut.from_before_today",
        $"{from:yyyy-MM-dd} has already passed - {today:yyyy-MM-dd} is today for this worker's calendar. " +
        "A re-cut can only start today or later.");

    /// <summary><paramref name="from"/> is at or past the schedule's own cursor, so there is nothing
    /// already materialised in <c>[from, cursor)</c> for this operation to undo. Moving the cursor
    /// forward, or leaving it where it is, is what <see cref="WorkerSchedule.ReconfigureWeekly"/>,
    /// <see cref="WorkerSchedule.ReconfigureCycle"/> and <see cref="WorkerSchedule.AdvanceCursor"/>
    /// already do.</summary>
    public static Error NotARegression(DateOnly from, DateOnly currentCursor) => new(
        "recut.not_a_regression",
        $"{from:yyyy-MM-dd} is not before this schedule's current cursor ({currentCursor:yyyy-MM-dd}); " +
        "there is nothing already cut in that range to re-cut. Save the schedule through the ordinary " +
        "path instead.");

    /// <summary>The schedule's own <see cref="WorkerSchedule.HorizonDays"/>, resolved fresh against
    /// today, ends before <paramref name="from"/> - e.g. the horizon was shrunk after the cursor last
    /// advanced, so nothing in <c>[from, horizon]</c> exists to re-cut at all under the *current*
    /// template. Narrowing the horizon is not this item's job to reclaim (`adr/0053`'s own "a widened
    /// horizon is not reclaimed by narrowing it" consequence) - it is simply a range with nothing left
    /// in it for this operation to act on.</summary>
    public static Error HorizonBeforeFrom(DateOnly from, DateOnly horizon) => new(
        "recut.horizon_before_from",
        $"The schedule's current horizon ({horizon:yyyy-MM-dd}) ends before {from:yyyy-MM-dd}; " +
        "there is nothing in range to re-cut.");

    /// <summary>The item's own central refusal: the set of bookings in <c>[from, horizon]</c> has
    /// changed since the preview this request's fingerprint came from - most likely a customer claimed
    /// a slot in between. Applying the operator's decisions to a booking they never saw is exactly the
    /// silent loss this item exists to prevent, so the whole request is refused rather than partially
    /// honoured.</summary>
    public static Error Stale() => new(
        "recut.stale",
        "The bookings in this range changed since the preview was generated - most likely a new " +
        "booking landed. Reload the preview and decide again.");

    /// <summary>A <see cref="EventStatus.PendingConfirmation"/> or <see cref="EventStatus.Booked"/>
    /// booking inside the requested range carries no cancel-or-keep decision. Refused rather than
    /// defaulted either way: guessing "keep" would silently leave a day the operator meant to fix
    /// untouched, and guessing "cancel" would silently cancel a customer's visit nobody chose to
    /// cancel.</summary>
    public static Error MissingDecision(EventId bookingId) => new(
        "recut.missing_decision",
        $"Booking {bookingId.Value} is in range and needs an explicit cancel-or-keep decision.");

    /// <summary>A new claim landed on this exact day in the narrow window between the staleness check
    /// above and this day's own write - the same residual <c>EditDayBoundaryHandler</c> and
    /// <c>DeleteDayOffHandler</c> already accept between their own pre-read and their own write. Days
    /// already re-cut earlier in this same request are not rolled back; see the handler's own remarks
    /// for why, and re-preview to pick up from here.</summary>
    public static Error DayChangedConcurrently(DateOnly localDate) => new(
        "recut.day_changed_concurrently",
        $"{localDate:yyyy-MM-dd} was booked while the re-cut was being applied. Days already re-cut in " +
        "this request stand; reload the preview to continue from here.");
}
