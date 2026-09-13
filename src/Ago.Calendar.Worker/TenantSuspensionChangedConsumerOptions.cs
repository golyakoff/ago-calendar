namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>TenantSuspensionChangedConsumer:*</c> config keys, matching
/// <see cref="ModuleQuantityGrantedConsumerOptions"/>'s own per-consumer options shape
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class TenantSuspensionChangedConsumerOptions
{
    public const string SectionName = "TenantSuspensionChangedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
