using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.RecutSchedule;

/// <summary>
/// The item's own chosen answer to its one open question: what the staleness check between preview
/// and confirm compares. A fingerprint over the booking ids and statuses in range - the cheap version
/// named in the backlog item, not a per-booking version number.
///
/// <para><b>Why the cheap version, deliberately.</b> A per-booking version number is the precise
/// alternative: it would let a confirm succeed even if some booking outside the operator's own
/// decisions changed state, by comparing only what the operator actually looked at. That precision
/// costs a version column (or a re-use of <c>xmin</c> exposed through a new read shape) that nothing
/// else in this product needs yet, for a use case this item's own scope calls "an operation this
/// destructive" and "one worker, one operation" - rare by design, not a hot path a spurious refusal
/// would make painful. The fingerprint can refuse a confirm when a booking merely changed status for a
/// reason unrelated to this re-cut (an operator elsewhere marked an unrelated no-show inside the
/// range); the item's own Open Questions section accepts that trade explicitly rather than paying for
/// precision nothing else needs.</para>
///
/// <para><b>Scoped to bookings only - <see cref="EventStatus.PendingConfirmation"/>,
/// <see cref="EventStatus.Booked"/> and <see cref="EventStatus.NoShow"/> - never <see cref="EventStatus.Available"/>,
/// <see cref="EventStatus.Blocked"/> or <see cref="EventStatus.Cancelled"/> rows.</b> Those three are
/// exactly what the item's own wording names ("a booking can land between the operator reading the
/// preview and pressing confirm"), and they are exactly what the day loop's own keep-or-cancel logic
/// cares about. A newly claimed slot changes this set (an <see cref="EventStatus.Available"/> row
/// becomes a <see cref="EventStatus.PendingConfirmation"/> one, which was not counted before and is
/// now), which is precisely the race this fingerprint exists to catch.</para>
///
/// <para><b>Deterministic across two different row shapes on purpose.</b> The preview handler computes
/// this over <c>WorkerSlotRow</c>s from the read side; the confirm handler computes it over
/// <see cref="Event"/> aggregates from the write side. Both reduce to the same
/// <c>(EventId, EventStatus)</c> pairs before hashing, so the two computations agree whenever the
/// underlying rows do, which is the whole point of comparing them at all.</para>
/// </summary>
internal static class RecutFingerprint
{
    public static string Compute(IEnumerable<(Guid BookingId, EventStatus Status)> bookings)
    {
        ArgumentNullException.ThrowIfNull(bookings);

        // Sorted so that the same set of bookings, read back in whatever order the database happened
        // to return them, always hashes to the same string - the comparison this exists for would be
        // meaningless against a fingerprint that depended on row order.
        var text = string.Join(
            ';',
            bookings
                .OrderBy(booking => booking.BookingId)
                .Select(booking => $"{booking.BookingId:D}:{booking.Status}"));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }
}
