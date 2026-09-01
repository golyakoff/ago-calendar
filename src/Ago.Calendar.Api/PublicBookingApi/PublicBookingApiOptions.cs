namespace Ago.Calendar.Api.PublicBookingApi;

/// <summary>
/// Bound from `PublicBookingApi:*` - the one host-level kill switch for `20-10`'s own public,
/// unauthenticated booking surface (<c>BookingEndpoints</c>'s <c>POST .../book</c> and
/// <c>PhoneVerificationEndpoints</c>'s initiate/confirm routes, which exist only to serve it).
///
/// <para><b>Decided 2026-09-01.</b> `20-10` built a real phone-verification mechanism for this
/// endpoint, but no real caller reaches it: <c>ago-widget</c> routes every booking through a chat
/// conversation (`20-07`), never this route directly. The endpoint and its verification primitive stay
/// in the codebase rather than being deleted - they may become a real product surface later, if AGO
/// Calendar ever publishes its booking API for third-party integration - but that product decision has
/// not been made, so the surface stays closed until it is. See <see cref="PublicBookingApiGate"/> for
/// where this is enforced and why closing it did not mean deleting `20-10`'s own code.</para>
///
/// <para><b>Why a plain bound <c>bool</c> rather than anything cleverer.</b> This is the exact shape
/// <c>PhoneVerificationOptions</c>/<c>PhoneVerificationRateLimitOptions</c> already establish for a
/// bound options class in this area of the codebase: a <c>SectionName</c> constant, plain
/// auto-properties, bound once at startup. Nothing here is a business rule Application could have an
/// opinion about - it is a fact about whether one HTTP host answers one group of routes at all - so
/// unlike those two classes this one is not shared with <c>Ago.Calendar.Worker</c> through
/// <c>CalendarModule</c>: only <c>Ago.Calendar.Api</c> maps these routes, so only
/// <c>Ago.Calendar.Api</c>'s own <c>Program.cs</c> binds it.</para>
/// </summary>
public sealed class PublicBookingApiOptions
{
    public const string SectionName = "PublicBookingApi";

    /// <summary>
    /// <see langword="false"/> unless a deployment's own configuration sets
    /// <c>PublicBookingApi__Enabled=true</c> - and <see langword="false"/> is also what an entirely
    /// absent <c>PublicBookingApi</c> section binds to, which is deliberate: nothing about turning this
    /// surface on can happen by omission. Reversibility is the whole design of this options class -
    /// none of `20-10`'s routes, handlers, the <c>PendingPhoneVerification</c> aggregate, or its
    /// migration were touched to close this surface, so reopening it later is flipping this one value,
    /// never resurrecting deleted code.
    /// </summary>
    public bool Enabled { get; set; }
}
