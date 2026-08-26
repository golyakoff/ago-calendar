namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>Bound from <c>Booking:*</c>, validated at startup (naming-and-structure.md's
/// options-validation rule).</summary>
public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    /// <summary>
    /// How long an operator has to veto a booking before `20-04`'s sweep confirms it automatically.
    ///
    /// <para><b>Fifteen minutes is a starting point and nothing measured it</b>, stated plainly for
    /// the same reason `20-02`'s rolling horizon says so: this product has no traffic, so it has no
    /// data on how quickly a real shop looks at its queue. The value is a configuration key
    /// precisely so the first tenant can move it without a deploy. What the number trades between is
    /// worth writing down even though the trade has not been measured - too short and a shop that
    /// steps away for coffee auto-confirms a booking it would have rejected; too long and a customer
    /// who was told "confirmed" waits that long before anything is actually settled, which is the
    /// visitor-facing half of the two-step mechanic and the reason the window cannot simply be made
    /// generous.</para>
    ///
    /// <para>It is a duration here and an absolute instant on the row: <c>Event.ConfirmationDeadline</c>
    /// is computed once at claim time, so `20-04`'s sweep filters on a plain <c>&lt;= now()</c>
    /// instead of an expression, and so that changing this setting never retroactively moves the
    /// deadline of a booking a customer has already been given.</para>
    /// </summary>
    public TimeSpan ConfirmationWindow { get; set; } = TimeSpan.FromMinutes(15);
}
