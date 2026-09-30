namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>WorkerScheduleSavedMaterializationConsumer:*</c> config keys, matching
/// <see cref="BookingPendingFanoutConsumerOptions"/>'s own per-consumer options shape
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class WorkerScheduleSavedMaterializationConsumerOptions
{
    public const string SectionName = "WorkerScheduleSavedMaterializationConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
