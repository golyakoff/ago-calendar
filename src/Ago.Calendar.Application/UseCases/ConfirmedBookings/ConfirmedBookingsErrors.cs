using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ConfirmedBookings;

/// <summary>
/// The expected failures of `23-34`'s own read, in the <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary
/// <c>ContactsErrors</c> and <c>WorkerSlotsErrors</c> already established.
/// </summary>
public static class ConfirmedBookingsErrors
{
    public static Error Forbidden(Permission permission) => new(
        "confirmed_bookings.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error InvalidRange(DateOnly from, DateOnly to) => new(
        "confirmed_bookings.invalid_range",
        $"The range must end on or after it starts; got {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}.");
}
