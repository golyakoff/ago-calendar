namespace Ago.Calendar.Domain;

/// <summary>
/// A customer took a free slot; the operator veto window is now open until
/// <paramref name="ConfirmationDeadline"/>. The first "your booking is received" SMS hangs off this
/// (`20-05`), through the outbox, in the same transaction as the claim itself (CLAUDE.md rule 4).
/// </summary>
public sealed record EventClaimed(
    EventId EventId,
    TenantId TenantId,
    CalendarId CalendarId,
    WorkerId WorkerId,
    ServiceId ServiceId,
    CustomerId CustomerId,
    TimeSlot Slot,
    DateTimeOffset ConfirmationDeadline,
    DateTimeOffset OccurredAt) : IDomainEvent;
