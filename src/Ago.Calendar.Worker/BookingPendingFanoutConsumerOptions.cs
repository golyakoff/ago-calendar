namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>BookingPendingFanoutConsumer:*</c> config keys, matching every other
/// consumer's own per-consumer options shape (naming-and-structure.md's options-validation rule).</summary>
public sealed class BookingPendingFanoutConsumerOptions
{
    public const string SectionName = "BookingPendingFanoutConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
