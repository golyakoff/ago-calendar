using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <see cref="EventConfirmed"/> and <see cref="BookingConfirmed"/> meet. clean-architecture.md: the
/// mapping happens in Application when writing to the outbox, and Domain and Contracts never share a
/// type. The same shape ago-chat's <c>MessageAcceptedMapper</c> established, reused rather than
/// reinvented - this is the first of these in AGO Calendar and it will not be the last.
/// </summary>
public static class BookingConfirmedMapper
{
    /// <param name="domainEvent">The anchor row's own <see cref="EventConfirmed"/> - its
    /// <see cref="EventConfirmed.EventId"/> is what <see cref="BookingConfirmed.EventId"/> carries,
    /// which `20-18` keeps meaning "the booking's identity" now that it is the run's anchor id rather
    /// than necessarily its only row.</param>
    /// <param name="idGenerator">Mints <see cref="BookingConfirmed.CorrelationId"/> - see the field's
    /// own remarks for why a fresh id rather than a threaded one.</param>
    /// <param name="groupEndsAt">
    /// `20-18`: the run's own last slot's end, when this confirmation is for a multi-slot booking.
    /// Defaults to <paramref name="domainEvent"/>'s own <see cref="EventConfirmed.Slot"/> end - correct
    /// for the anchor's own single slot and for every booking that is only one slot long, which is why
    /// existing callers of this method compile and behave unchanged. A caller emitting one message per
    /// *group* rather than per row (<c>ExpiredBookingConfirmer</c>) supplies the whole run's end here,
    /// so a customer told "12:00-13:10" is told about the booking they made, not about its first slot.
    /// </param>
    public static EventEnvelope ToEnvelope(
        EventConfirmed domainEvent, IIdGenerator idGenerator, DateTimeOffset? groupEndsAt = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(idGenerator);

        var contract = new BookingConfirmed(
            EventId: domainEvent.EventId.Value,
            TenantId: domainEvent.TenantId.Value,
            CalendarId: domainEvent.CalendarId.Value,
            CustomerId: domainEvent.CustomerId.Value,
            StartsAt: domainEvent.Slot.StartsAt,
            EndsAt: groupEndsAt ?? domainEvent.Slot.EndsAt,
            LocalDate: domainEvent.LocalDate,
            OccurredAt: domainEvent.OccurredAt,
            // No request-scoped correlation reaches a background sweep - the tick that confirms a
            // booking is not the request that made it, and borrowing the claim's correlation id would
            // imply a causal link through a deadline that nobody caused. A fresh id is the honest
            // choice, the same call MessageAcceptedMapper made for the same reason.
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt));

        return new EventEnvelope(
            // The event's own id, not a second one: an outbox row and the fact it carries are the
            // same thing, and a consumer deduplicating on MessageId is deduplicating on "this
            // booking was confirmed" - which is exactly the idempotency key it wants, since a
            // redelivered confirmation for one booking must be a no-op (CLAUDE.md rule 5).
            MessageId: contract.EventId,
            Type: nameof(BookingConfirmed),
            Version: 1,
            // Per booking, not per tenant. messaging.md guarantees order per partition key and never
            // globally; the only ordering that matters here is between events about one booking
            // (confirmed, then later cancelled), and a tenant-wide key would serialise every one of a
            // busy shop's confirmations behind each other for a guarantee nobody needs.
            PartitionKey: contract.EventId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
