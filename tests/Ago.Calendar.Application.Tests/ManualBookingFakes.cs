using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `26-268`/`adr/0188`: the manual-entry store, faked - the identical shape
/// <c>BookingLifecycleFakes.FakeBookingRescheduleStore</c> already establishes for its own sibling
/// port. <see cref="Attempts"/> is the assertion surface for the positive cases (which run was claimed,
/// which two envelopes it carried) and <see cref="Called"/> is the surface for the negative ones - a
/// refused entry must never reach the store at all.
/// </summary>
internal sealed class FakeManualBookingStore : IManualBookingStore
{
    public List<ManualBookingAttempt> Attempts { get; } = [];

    /// <summary>Default true - the ordinary "the run was claimable" path. Set false to model the claim
    /// losing the race, the ordinary outcome that stages nothing.</summary>
    public bool SlotIsClaimable { get; set; } = true;

    public Task<BookingConfirmation?> TryEnterAsync(
        ManualBookingAttempt attempt, CancellationToken cancellationToken)
    {
        Attempts.Add(attempt);

        if (!SlotIsClaimable)
        {
            return Task.FromResult<BookingConfirmation?>(null);
        }

        return Task.FromResult<BookingConfirmation?>(new BookingConfirmation(
            attempt.EventIds[0],
            attempt.EventIds,
            attempt.PersonId,
            BookingFixtures.WorkerId,
            BookingFixtures.Slot,
            BookingFixtures.LocalDate));
    }
}
