namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>PendingBookingSweepJob:*</c>, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class PendingBookingSweepJobOptions
{
    public const string SectionName = "PendingBookingSweepJob";

    /// <summary>
    /// How often the sweep runs.
    ///
    /// <para><b>Thirty seconds, and unlike this repository's other two jobs this one has a reason to
    /// be short rather than a shrug.</b> The interval is the worst-case lateness of a confirmation: a
    /// booking whose deadline passes one instant after a tick waits a whole interval before its
    /// <c>BookingConfirmed</c> is staged, and `20-05` hangs an SMS off that event. Daily - which is
    /// right for <c>AvailabilityMaterializationJob</c>, whose unit of work is a whole day - would mean
    /// a customer waiting up to a day for the message telling them their booking is settled.</para>
    ///
    /// <para>Still unmeasured, and the number is a judgement rather than a measurement: it is short
    /// enough that the delay is invisible next to the confirmation window itself (fifteen minutes by
    /// default, `20-03`) and long enough that a quiet deployment is not running a query per tenant
    /// every second for nothing. `4-02`'s own assignment loop picked two seconds for a job whose
    /// latency a human is actively waiting on; nobody is waiting on this one.</para>
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many bookings one transaction confirms.
    ///
    /// <para>The bound exists to keep one transaction short, which is what keeps the row locks it
    /// holds short - the same reason `4-02` batches its assignment claim and `2-04` its outbox claim.
    /// A backlog larger than this simply takes several ticks, in deadline order, which is the
    /// behaviour a bound is supposed to produce. Unmeasured.</para>
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How many tenants one keyset page fetches while the sweep walks every tenant. Matches
    /// <c>AvailabilityMaterializationJobOptions.TenantPageSize</c>; nothing measured either.</summary>
    public int TenantPageSize { get; set; } = 100;
}
