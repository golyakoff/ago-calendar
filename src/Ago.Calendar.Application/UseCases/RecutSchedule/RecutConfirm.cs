using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>An operator's choice for one booking a re-cut found in range. <see cref="Cancel"/> goes
/// through <see cref="CancelBookingHandler"/>, exactly like any other operator-initiated cancellation;
/// <see cref="Keep"/> leaves the booking, and its whole day, untouched in the old grid.</summary>
public enum RecutDecision
{
    Cancel,
    Keep,
}

/// <param name="BookingId">Must be a <see cref="EventStatus.PendingConfirmation"/> or
/// <see cref="EventStatus.Booked"/> row inside <c>[From, today + HorizonDays]</c> - a decision for
/// anything else (a different day's booking, a <see cref="EventStatus.NoShow"/> row, an id that no
/// longer exists) is silently ignored rather than refused: extra, no-longer-relevant decisions are the
/// ordinary shape of a console re-submitting a form built from a slightly stale preview payload, and
/// refusing on that basis would be pickier than what matters - see <see cref="RecutErrors.MissingDecision"/>
/// for the one direction this handler does refuse on.</param>
public readonly record struct RecutBookingDecision(EventId BookingId, RecutDecision Decision);

/// <summary>
/// <c>POST /workers/{workerId}/schedule/recut</c>: apply the decisions an operator made against a
/// <see cref="RecutPreviewResult"/>, move the cursor to <paramref name="From"/>, and regenerate every
/// day that ends up with nothing left to protect it.
/// </summary>
/// <param name="Fingerprint">Handed back unchanged from the <see cref="RecutPreviewResult"/> this
/// request is acting on. Recomputed fresh over the current booking set and compared before any write -
/// a mismatch refuses the whole request (<see cref="RecutErrors.Stale"/>).</param>
public readonly record struct RecutConfirm(
    OperatorId OperatorId,
    TenantId TenantId,
    WorkerId WorkerId,
    DateOnly From,
    string Fingerprint,
    IReadOnlyList<RecutBookingDecision> Decisions);

/// <param name="RecutDays">Cleared of every <see cref="EventStatus.Available"/>/<see cref="EventStatus.Blocked"/>
/// row and regenerated from the schedule's current template - either because nothing held them, or
/// because every booking on them was decided <see cref="RecutDecision.Cancel"/>.</param>
/// <param name="SkippedDays">Left entirely in the old grid, untouched, because at least one booking on
/// the day was decided <see cref="RecutDecision.Keep"/> or was a <see cref="EventStatus.NoShow"/> row
/// that cannot be decided at all. <see cref="RecutDays"/> and <see cref="SkippedDays"/> partition
/// <c>[From, today + HorizonDays]</c> exactly - every day in range is in exactly one of the two.</param>
public readonly record struct RecutConfirmResult(
    IReadOnlyList<DateOnly> RecutDays,
    IReadOnlyList<DateOnly> SkippedDays,
    int SlotsDeleted,
    int SlotsInserted,
    int BookingsCancelled);
