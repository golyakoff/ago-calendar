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

    /// <summary>How many tenants one keyset page fetches while the job walks every tenant. Sized so
    /// that a page is a small query rather than a small number of queries; nothing measured it.</summary>
    public int TenantPageSize { get; set; } = 100;
}
