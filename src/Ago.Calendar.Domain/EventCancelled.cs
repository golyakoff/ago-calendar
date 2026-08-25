namespace Ago.Calendar.Domain;

/// <summary>A booking was withdrawn - see <see cref="CancellationReason"/> for why the two ways of
/// getting here stay distinguishable.</summary>
public sealed record EventCancelled(
    EventId EventId,
    TenantId TenantId,
    CustomerId? CustomerId,
    TimeSlot Slot,
    CancellationReason Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;
