using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>
/// <c>POST /workers/{workerId}/schedule/recut/preview</c>: "if I moved this worker's cursor back to
/// <paramref name="From"/> right now, what would disappear, and who is on the days that would?"
/// </summary>
/// <param name="From">The date the operator wants the cursor moved back to - must be today or later
/// for the worker's own calendar zone, and strictly before the schedule's current
/// <see cref="WorkerSchedule.MaterializeFrom"/> (<see cref="RecutErrors.NotARegression"/> otherwise).
/// </param>
public readonly record struct RecutPreview(OperatorId OperatorId, TenantId TenantId, WorkerId WorkerId, DateOnly From);

/// <param name="Days">Every business-local day in <c>[From, today + HorizonDays]</c>, oldest first -
/// including a day with nothing on it at all, because that day is still going to be freshly cut and an
/// operator scanning a 180-day range benefits from a complete, boring list over a filtered one that
/// hides how large the range actually is.</param>
/// <param name="Fingerprint">Opaque to the caller - hands it back unchanged on
/// <see cref="RecutConfirm"/>, and <see cref="RecutFingerprint"/> is the only code that reads its
/// shape.</param>
public readonly record struct RecutPreviewResult(
    IReadOnlyList<RecutDayPreview> Days, string Fingerprint);

/// <param name="AvailableSlotsToDelete">How many <see cref="EventStatus.Available"/> rows a confirm
/// would delete on this day - zero for a day that has not been materialised at all yet, which is still
/// a day the confirm will freshly cut.</param>
/// <param name="Bookings">Every <see cref="EventStatus.PendingConfirmation"/>,
/// <see cref="EventStatus.Booked"/> or <see cref="EventStatus.NoShow"/> row on this day. A non-empty
/// list here is what forces the day to <see cref="RecutConfirmResult.SkippedDays"/> unless every
/// decidable booking on it is decided <c>Cancel</c> - see <see cref="RecutBookingPreview.CanDecide"/>.
/// </param>
public readonly record struct RecutDayPreview(
    DateOnly LocalDate, int AvailableSlotsToDelete, IReadOnlyList<RecutBookingPreview> Bookings);

/// <param name="CustomerId">Not personal data itself - a foreign key - so never gated, the same
/// distinction <see cref="WorkerSlotRow.CustomerId"/> draws from <see cref="CustomerDisplayName"/> and
/// <see cref="Phone"/>.</param>
/// <param name="CustomerDisplayName">Null either because this operator does not hold
/// <see cref="Permission.CustomerRead"/> for this tenant, or (impossible here, since every row in this
/// list holds a customer by construction) for the "nobody holds it" reason
/// <see cref="WorkerSlotRow.CustomerDisplayName"/> also carries - kept as the identical two-null-reasons
/// shape rather than inventing a narrower one for this one caller.</param>
/// <param name="CanDecide"><see langword="false"/> only for a <see cref="EventStatus.NoShow"/> row: a
/// visit that already happened cannot be cancelled through <see cref="CancelBookingHandler"/>, whose
/// state machine accepts only <see cref="EventStatus.PendingConfirmation"/> and
/// <see cref="EventStatus.Booked"/>. A <c>NoShow</c> row therefore always forces its day into
/// <see cref="RecutConfirmResult.SkippedDays"/> regardless of what <see cref="RecutConfirm.Decisions"/>
/// says about it - the console should not offer a control for it at all, which is why the flag exists
/// rather than leaving the console to infer it from <see cref="Status"/> on its own.</param>
public readonly record struct RecutBookingPreview(
    EventId BookingId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    EventStatus Status,
    ServiceId? ServiceId,
    string? ServiceName,
    CustomerId? CustomerId,
    string? CustomerDisplayName,
    PhoneNumber? Phone,
    bool CanDecide);
