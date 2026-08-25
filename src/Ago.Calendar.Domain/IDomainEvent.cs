namespace Ago.Calendar.Domain;

/// <summary>
/// An internal fact this product's own aggregates raise, never a wire contract
/// (clean-architecture.md). <c>Ago.Calendar.Contracts</c> holds the published shape, and the two are
/// mapped deliberately - `20-05`'s SMS integration event is the first mapping that will exist.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
