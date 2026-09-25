namespace Ago.Calendar.Domain;

/// <summary>A confirmed visit that already ended, marked as not attended. Its only consumer today is
/// the person record's own <see cref="PersonRecord.NoShowCount"/> (`20-04`); no SMS follows a no-show.</summary>
public sealed record EventNoShowRecorded(
    EventId EventId,
    TenantId TenantId,
    Guid PersonId,
    TimeSlot Slot,
    DateTimeOffset OccurredAt) : IDomainEvent;
