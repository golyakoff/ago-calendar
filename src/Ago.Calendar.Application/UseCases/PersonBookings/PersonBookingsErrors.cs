using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PersonBookings;

/// <summary>
/// The expected failures of `26-269`'s own read, in the <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary
/// <c>ConfirmedBookingsErrors</c> and <c>ContactsErrors</c> already established. Just the one arm - this
/// read has no range to be malformed, so it has no second producer the way its tenant-wide sibling does.
/// </summary>
public static class PersonBookingsErrors
{
    public static Error Forbidden(Permission permission) => new(
        "person_bookings.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");
}
