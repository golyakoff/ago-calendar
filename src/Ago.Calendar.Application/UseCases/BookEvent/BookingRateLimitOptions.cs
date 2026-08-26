namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// Bound from <c>BookingRateLimit:*</c> - the same `3-05` shape every rate-limit options class in
/// AGO Chat already uses, reused here rather than reinvented.
///
/// <para><b>Why this endpoint is the one that needs it most.</b> Everything else in either product
/// that writes a row requires either a signed visitor token or an operator login. Booking requires
/// neither by design: a customer books with a phone number and no account. So this is an
/// unauthenticated endpoint that creates rows - a lead card holding personal data, and a state
/// transition on a slot that takes it out of circulation for everybody else. Both are worth abusing:
/// the second is a denial-of-service against a real shop's whole day, achievable with nothing but a
/// list of event ids.</para>
///
/// <para>Every number below is a starting point, unmeasured, and the file says so rather than
/// implying otherwise (CLAUDE.md: "measure or stay silent"). What they are *shaped* like is
/// argued: a person books rarely and in bursts of one, so the per-phone bucket is small with a slow
/// refill; a calendar legitimately receives many bookings from many people, so its bucket is much
/// larger and exists to bound a flood rather than to pace a human.</para>
/// </summary>
public sealed class BookingRateLimitOptions
{
    public const string SectionName = "BookingRateLimit";

    /// <summary>Per phone number, per tenant. Deliberately tenant-scoped: the same person booking at
    /// two different shops within a minute is ordinary behaviour, and one shared bucket would make
    /// one shop's customer throttle another's.</summary>
    public int PerPhoneCapacity { get; set; } = 5;

    /// <summary>Five per hour. A customer correcting a mistake or booking a second appointment is
    /// well inside this; a script walking event ids is not.</summary>
    public double PerPhoneRefillPerSecond { get; set; } = 5.0 / 3600;

    /// <summary>Per calendar - the bucket that bounds a flood arriving from many phone numbers at
    /// once, which the per-phone bucket cannot see. Coarse on purpose: a busy salon's real booking
    /// rate must never touch it.</summary>
    public int PerCalendarCapacity { get; set; } = 120;

    /// <summary>Two per second sustained.</summary>
    public double PerCalendarRefillPerSecond { get; set; } = 2.0;
}
