namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>ContactCollectedConsumer:*</c> config keys, matching every other consumer's
/// own per-consumer options shape (naming-and-structure.md's options-validation rule).</summary>
public sealed class ContactCollectedConsumerOptions
{
    public const string SectionName = "ContactCollectedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
