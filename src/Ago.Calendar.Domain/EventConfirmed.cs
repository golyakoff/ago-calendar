namespace Ago.Calendar.Domain;

/// <summary>The veto window closed without a rejection, or an operator confirmed early - the visit
/// is on. The second SMS (`20-05`) follows this one.</summary>
public sealed record EventConfirmed(
    EventId EventId,
    TenantId TenantId,
    CustomerId CustomerId,
    TimeSlot Slot,
    DateTimeOffset OccurredAt) : IDomainEvent;
