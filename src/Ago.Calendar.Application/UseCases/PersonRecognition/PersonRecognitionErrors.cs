using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PersonRecognition;

/// <summary>
/// The expected failures of `26-268`§2a's own read, in the <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary
/// <c>PersonBookingsErrors</c> and <c>BookingErrors</c> already established.
/// </summary>
public static class PersonRecognitionErrors
{
    public static Error Forbidden(Permission permission) => new(
        "person_recognition.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    /// <summary>The operator's own typed number does not parse as a phone - the identical rejection
    /// <c>BookingErrors.InvalidPhone</c> already gives for the same
    /// <see cref="PhoneNumber"/> constructor failure, restated in this read's own vocabulary.</summary>
    public static Error InvalidPhone(string reason) => new("person_recognition.invalid_phone", reason);
}
