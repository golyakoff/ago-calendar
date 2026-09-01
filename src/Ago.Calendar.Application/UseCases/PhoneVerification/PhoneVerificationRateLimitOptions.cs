namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <summary>
/// Bound from `PhoneVerificationRateLimit:*` - the same multi-bucket shape `BookingRateLimitOptions`
/// and `ago-chat`'s own `PhoneVerificationRateLimitOptions` (`14-15`) already establish, adapted here
/// for a caller with no visitor/site concept.
///
/// <para><b>Why "phone + calling IP", not "phone + visitor", per this item's own brief.</b> `14-15`'s
/// two threats - many attempts against <em>one</em> phone number (harassment), and one caller
/// iterating through <em>many</em> numbers (enumeration) - both still apply here, but the public
/// booking widget is reached anonymously with no session and no visitor id to key the second bucket
/// on. The calling IP is this endpoint's only other caller-identifying fact, the same one
/// <c>BookEventHandler</c>'s own per-calendar bucket would see flood from if a script tried many
/// numbers against one shop. It is an imperfect substitute - shared/NAT'd IPs exist - but it is the
/// only substitute this trust boundary actually has, and it is stated as exactly that rather than
/// implied to be as precise as a real visitor id.</para>
///
/// <para><see cref="PerPhoneCapacity"/> checked first, then <see cref="PerIpCapacity"/>, then
/// <see cref="PerCalendarCapacity"/> last - the identical "cheapest, most caller-specific bucket first;
/// a caller who was never going to pass their own bucket should not also spend a share of a coarser
/// one finding that out" ordering both `14-15`'s own options class and <c>BookEventHandler</c>'s own
/// per-phone-then-per-calendar order already establish.</para>
///
/// <para>Every number below is a starting point, unmeasured (CLAUDE.md: "measure or stay silent").</para>
/// </summary>
public sealed class PhoneVerificationRateLimitOptions
{
    public const string SectionName = "PhoneVerificationRateLimit";

    public int PerPhoneCapacity { get; set; } = 3;

    public double PerPhoneRefillPerSecond { get; set; } = 3.0 / 3600;

    public int PerIpCapacity { get; set; } = 10;

    public double PerIpRefillPerSecond { get; set; } = 10.0 / 3600;

    public int PerCalendarCapacity { get; set; } = 100;

    public double PerCalendarRefillPerSecond { get; set; } = 100.0 / 3600;
}
