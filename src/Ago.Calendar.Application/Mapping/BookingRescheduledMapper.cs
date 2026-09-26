using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>
/// `26-208`/`adr/0187`: builds the one <see cref="EventEnvelope"/> a reschedule stages - the same
/// "domain fact -> integration event -> <see cref="EventEnvelope"/>, in one place" shape
/// <see cref="BookingConfirmedMapper"/> and <see cref="BookingPendingStateChangedMapper"/> already
/// establish, and Domain and Contracts never share a type (clean-architecture.md).
///
/// <para><b>Raw values, not a domain event, as this method's input - the identical reason
/// <see cref="BookingPendingStateChangedMapper"/> gives.</b> A reschedule is a handler-orchestrated
/// composition of transitions that already exist (`adr/0187`: cancel the old run, claim the new one),
/// not a new domain method, so there is no single <c>BookingRescheduled</c> domain event to map from -
/// the two halves that exist (<see cref="EventCancelled"/> on the old run, and the new run's raw claim,
/// which never builds an <see cref="Event"/> aggregate at all) each carry only part of the fact, and
/// neither carries the old->new link this contract's whole reason for existing (`adr/0187`) is to hold.
/// The plain-value signature below is the one shape the reschedule handler can supply from the two
/// runs it already resolved.</para>
///
/// <para><b><see cref="EventEnvelope.MessageId"/> is the new booking's own anchor id.</b> Like
/// <see cref="BookingConfirmedMapper"/> and unlike <see cref="BookingPendingStateChangedMapper"/>: a
/// given new run is claimed once and can be rescheduled at most once more (into a further new anchor),
/// so <c>BookingRescheduled</c> is staged at most once per <paramref name="newBookingId"/>. Reusing it
/// as the <c>MessageId</c> makes a redelivered reschedule a no-op on the consumer's own
/// <c>MessageId</c>-keyed idempotency (CLAUDE.md rule 5), which is exactly the dedup key a consumer
/// wants for "this booking moved".</para>
/// </summary>
public static class BookingRescheduledMapper
{
    public static EventEnvelope ToEnvelope(
        EventId newBookingId,
        EventId previousBookingId,
        TenantId tenantId,
        CalendarId calendarId,
        Guid personId,
        DateTimeOffset previousStartsAt,
        DateTimeOffset newStartsAt,
        DateTimeOffset newEndsAt,
        DateOnly localDate,
        DateTimeOffset occurredAt,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);

        var correlationId = idGenerator.NewId(occurredAt);
        var contract = new BookingRescheduled(
            EventId: newBookingId.Value,
            PreviousEventId: previousBookingId.Value,
            TenantId: tenantId.Value,
            CalendarId: calendarId.Value,
            PersonId: personId,
            PreviousStartsAt: previousStartsAt,
            NewStartsAt: newStartsAt,
            NewEndsAt: newEndsAt,
            LocalDate: localDate,
            OccurredAt: occurredAt,
            CorrelationId: correlationId);

        return new EventEnvelope(
            // The new booking's own id, the same "an outbox row and the fact it carries are one thing"
            // reasoning BookingConfirmedMapper gives - here keyed to the new anchor because that is the
            // booking this fact is primarily about.
            MessageId: contract.EventId,
            Type: nameof(BookingRescheduled),
            Version: 1,
            // Per booking, not per tenant - messaging.md guarantees order per partition key, never
            // globally, and the only ordering that matters is between events about one booking. Keyed
            // to the new anchor for the same reason MessageId is.
            PartitionKey: contract.EventId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
