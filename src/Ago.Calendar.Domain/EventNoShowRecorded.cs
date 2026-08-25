namespace Ago.Calendar.Domain;

/// <summary>A confirmed visit that already ended, marked as not attended. Its only consumer today is
/// the lead card's own <see cref="Customer.NoShowCount"/> (`20-04`); no SMS follows a no-show.</summary>
public sealed record EventNoShowRecorded(
    EventId EventId,
    TenantId TenantId,
    CustomerId CustomerId,
    TimeSlot Slot,
    DateTimeOffset OccurredAt) : IDomainEvent;
