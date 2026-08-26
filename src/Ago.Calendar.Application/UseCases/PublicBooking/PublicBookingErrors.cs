using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PublicBooking;

/// <summary>
/// The expected failures of the unauthenticated booking surface, in the same
/// <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary the rest of this product uses.
///
/// <para><b>Vague on purpose, exactly like `20-03`'s <c>BookingErrors</c> and unlike `20-04`'s
/// operator-facing ones.</b> Every caller here is a stranger on the public internet. "That tenant
/// does not exist", "that tenant exists but has not published a calendar" and "that calendar belongs
/// to somebody else" are three different sentences that between them let anyone enumerate this
/// product's customers, so they are one sentence. The origin rejection is the same: a page told
/// "your origin is not approved for this tenant" has learned that the tenant exists.</para>
/// </summary>
public static class PublicBookingErrors
{
    public static Error NotFound() => new(
        "booking.surface_not_found",
        "No published booking surface answers to that key.");

    /// <summary><b>Layer 2's rejection.</b> Deliberately identical to <see cref="NotFound"/> in
    /// everything a caller can see except the code, which exists so this product's own logs and tests
    /// can tell the two apart. A distinct HTTP status would leak the same fact the message refuses
    /// to.</summary>
    public static Error OriginNotAllowed() => new(
        "booking.origin_not_allowed",
        "No published booking surface answers to that key.");
}
