namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>ModuleQuantityImpactRequestedConsumer:*</c> config keys, matching
/// <see cref="ModuleQuantityGrantedConsumerOptions"/>'s own per-consumer options shape
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ModuleQuantityImpactRequestedConsumerOptions
{
    public const string SectionName = "ModuleQuantityImpactRequestedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
