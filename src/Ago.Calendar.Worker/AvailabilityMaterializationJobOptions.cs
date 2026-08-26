namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>AvailabilityMaterializationJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class AvailabilityMaterializationJobOptions
{
    public const string SectionName = "AvailabilityMaterializationJob";

    /// <summary>
    /// How often the horizon is pushed forward. Daily, matching <c>PartitionMaintenanceJob</c>'s own
    /// precedent and for the same reason: the thing being kept ahead of need moves by whole days, so
    /// a shorter interval only re-asks a question whose answer cannot have changed. Explicitly an
    /// unmeasured starting point, not a tuned number - a missed run or two is harmless, because the
    /// next one covers the whole window rather than only the day it would have added.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How many business-local days past today always have slots generated.
    ///
    /// <para><b>Thirty is a starting point, not a measurement, and this is the honest version of
    /// that.</b> The only real constraint is that the horizon exceeds how far ahead customers
    /// actually book, and this product has no traffic and therefore no data on that. Thirty was
    /// chosen because "about a month" is legible to whoever reads this file, and it is a
    /// configuration key precisely so that the first real tenant can move it without a deploy.
    /// CLAUDE.md's rule against inventing numbers is why there is no claim here that it is right -
    /// `3-05`'s rate-limit buckets set the same precedent of a documented, unmeasured default.</para>
    ///
    /// <para>Widening the window costs one row per slot per worker per added day, and it is not
    /// reclaimed by narrowing it again: the materialiser never deletes, so a horizon that has been
    /// pushed out stays out.</para>
    /// </summary>
    public int HorizonDays { get; set; } = 30;

    /// <summary>How many tenants one keyset page fetches while the job walks every tenant. Sized so
    /// that a page is a small query rather than a small number of queries; nothing measured it.</summary>
    public int TenantPageSize { get; set; } = 100;
}
