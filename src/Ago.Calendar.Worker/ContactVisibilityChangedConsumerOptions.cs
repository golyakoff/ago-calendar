namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>ContactVisibilityChangedConsumer:*</c> config keys, matching
/// <see cref="RoleAssignmentsChangedConsumerOptions"/>'s own per-consumer options shape
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ContactVisibilityChangedConsumerOptions
{
    public const string SectionName = "ContactVisibilityChangedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
