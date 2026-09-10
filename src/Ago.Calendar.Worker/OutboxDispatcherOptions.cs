namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>OutboxDispatcher:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the identical shape
/// `Ago.Chat.Worker.OutboxDispatcherOptions` already establishes, restated here for this product's own
/// host rather than shared, for the same "host-shaped wiring, not a platform mechanism" reason
/// `ClaimedOutboxRow`'s own remarks give.</summary>
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "OutboxDispatcher";

    /// <summary>Fallback only - messaging.md: LISTEN/NOTIFY wakes the dispatcher immediately on a
    /// fresh row; this interval only matters for a missed or coalesced notification.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 20;

    /// <summary>resilience.md: "Timeout, retry with jittered backoff, publisher confirms" for the
    /// RabbitMQ boundary - a publisher-confirmed publish against an unresponsive broker (paused,
    /// network-partitioned) waits for a confirm that will never come otherwise, blocking this whole
    /// batch forever instead of failing the one row and moving on. A timed-out row is not retried on
    /// a fixed schedule - it stays unpublished until the next dispatch cycle claims it again,
    /// indefinitely, for as long as the broker stays down (the outbox accumulates rather than gives
    /// up - resilience.md), so a broker outage has no honest upper-bound delay, only "until the
    /// broker is back".</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
