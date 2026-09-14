namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>SiteErasedConsumer:*</c> config keys, matching
/// <see cref="RoleAssignmentsChangedConsumerOptions"/>'s own per-consumer options shape
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class SiteErasedConsumerOptions
{
    public const string SectionName = "SiteErasedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
