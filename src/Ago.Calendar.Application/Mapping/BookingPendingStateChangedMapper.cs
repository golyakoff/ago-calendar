using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>
/// `25-63`: builds the one envelope every "entering or leaving pending" call site stages - the same
/// "domain event -> integration event -> <see cref="EventEnvelope"/>, in one place" shape
/// <see cref="BookingConfirmedMapper"/> already establishes.
///
/// <para><b>Raw values, not a domain event, as this method's input - unlike <see cref="BookingConfirmedMapper"/>.</b>
/// <see cref="EventConfirmed"/> exists and is genuinely raised on the path that confirms a booking, so
/// that mapper can take one. There is no equivalent single domain event this method could take: the
/// real claim path (<c>Ago.Calendar.Infrastructure.Postgres.BookingStore</c>, an Infrastructure type
/// this Application-layer file must not reference directly, named here only in prose) is a raw
/// <c>UPDATE ... RETURNING</c> that never builds an <see cref="Event"/> aggregate at
/// all - <see cref="Event.Claim"/> and its own <see cref="EventClaimed"/> exist but are unreachable
/// from the real, live claim (verified by grep, not assumed: nothing outside <c>Event.cs</c> itself
/// calls <see cref="Event.Claim"/>) - and <see cref="EventCancelled"/> carries no
/// <see cref="CalendarId"/>, which this contract does not need but a typed overload keyed to that
/// record would still have to accept or ignore. A plain <c>(bookingId, tenantId, status, occurredAt)</c>
/// tuple is the one shape every call site - the raw-SQL claim, the sweep's own EF transition, and the
/// two operator-driven EF transitions - can supply without inventing a fact it does not already
/// have.</para>
///
/// <para><b><see cref="EventEnvelope.MessageId"/> is a fresh id, never <paramref name="bookingId"/>.</b>
/// <see cref="BookingConfirmedMapper"/> reuses the booking's own id because <c>BookingConfirmed</c> can
/// only ever be staged once per booking (confirmation is a one-way door). This event is not: the same
/// booking id can appear here twice - once entering pending, once leaving it - and reusing the id would
/// make the outbox's own <c>MessageId</c>-keyed idempotency treat the second, genuinely different fact
/// as a redelivery of the first and drop it.</para>
/// </summary>
public static class BookingPendingStateChangedMapper
{
    public static EventEnvelope ToEnvelope(
        EventId bookingId, TenantId tenantId, string status, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(idGenerator);

        var correlationId = idGenerator.NewId(occurredAt);
        var contract = new BookingPendingStateChanged(bookingId.Value, tenantId.Value, status, occurredAt, correlationId);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(BookingPendingStateChanged),
            Version: 1,
            // Per booking, the same reasoning BookingConfirmedMapper's own PartitionKey remark gives:
            // messaging.md guarantees order per partition key, never globally, and the only ordering
            // that matters here is between this one booking's own transitions (entering, then later
            // leaving), never across a tenant's unrelated bookings.
            PartitionKey: bookingId.Value.ToString(),
            OccurredAt: occurredAt,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
