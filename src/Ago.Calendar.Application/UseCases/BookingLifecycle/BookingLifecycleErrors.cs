using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// The expected failures of the three operator-facing transitions, in the same
/// <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary `20-02`'s <c>AvailabilityErrors</c> and `20-03`'s
/// <c>BookingErrors</c> established.
///
/// <para><b>Unlike `20-03`'s errors, these are precise about why.</b> That endpoint is
/// unauthenticated, so a distinguishing message would have answered questions a stranger has no
/// business asking. These three are operator-only and already past a permission check, so the caller
/// is a person who is entitled to know the difference between "somebody cancelled it a moment ago"
/// and "that visit has not happened yet" - and who cannot act correctly without it.</para>
/// </summary>
public static class BookingLifecycleErrors
{
    public static Error Forbidden(Permission permission) => new(
        "booking.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error NotFound(EventId eventId) => new(
        "booking.not_found", $"Booking {eventId.Value} does not exist.");

    /// <summary>A booking that belongs to another tenant is reported as absent rather than as
    /// forbidden - the one place these errors stay vague, because an operator of tenant A learning
    /// that an id exists in tenant B is a cross-tenant leak however politely it is worded.</summary>
    public static Error WrongTenant(EventId eventId) => NotFound(eventId);

    public static Error InvalidState(string reason) => new("booking.invalid_state", reason);

    /// <summary>`26-208`/`adr/0187`: the new time an operator picked for a reschedule was taken,
    /// blocked, started, or otherwise not a legal run by the time the atomic claim ran - the ordinary
    /// lost-race outcome, generalised from the visitor booking path. The whole reschedule rolled back
    /// and the old booking stays <c>Booked</c>, untouched; the operator picks another time and
    /// retries. Precise, unlike the public booking surface's vague equivalent, because the caller here
    /// is an authenticated operator entitled to know why.</summary>
    public static Error SlotNoLongerAvailable() => new(
        "booking.slot_unavailable",
        "The new time is no longer available. Pick another and try again.");

    /// <summary>`26-208`/`adr/0187`: v1 reschedule is same-worker, same-service, time-only, so the
    /// target slot must belong to the same worker (and calendar) as the booking being moved. A target
    /// on a different worker is refused rather than silently moving across workers - a cross-worker
    /// move is a deliberately deferred capability (`adr/0187` §Consequences: cheap to add later,
    /// because claim-new can already target any slot in the grid).</summary>
    public static Error DifferentWorker() => new(
        "booking.invalid_state",
        "A reschedule must stay on the same worker. Cancel and rebook to move to a different worker.");

    public static Error ConcurrencyConflict(EventId eventId) => new(
        "booking.concurrency_conflict",
        $"Booking {eventId.Value} changed while you were acting on it. Reload it and try again.");

    /// <summary>`26-268`§2a/`adr/0188`: the operator's own «Это он» reuse named a person id that either
    /// does not exist or belongs to another tenant. Collapsed into one not-found, the identical
    /// cross-tenant info-hiding shape <see cref="WrongTenant"/> above and
    /// <c>ContactsErrors.CustomerNotFound</c> both already use - an operator of tenant A must not learn
    /// that a person id is real but belongs to tenant B.</summary>
    public static Error PersonNotFound(Guid personId) => new(
        "booking.person_not_found", $"Person {personId} does not exist in this tenant.");
}
