namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>RoleAssignmentsChangedConsumer:*</c> config keys, matching
/// `Ago.Chat.Worker`'s own per-consumer options shape (naming-and-structure.md's
/// options-validation rule).</summary>
public sealed class RoleAssignmentsChangedConsumerOptions
{
    public const string SectionName = "RoleAssignmentsChangedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
