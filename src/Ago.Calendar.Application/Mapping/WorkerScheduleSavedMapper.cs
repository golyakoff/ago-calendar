using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Mapping;

/// <summary>
/// `26-315`: builds the one envelope <see cref="Configuration.SaveWorkerScheduleHandler"/> stages on
/// every successful create-or-reconfigure - the same "domain fact -&gt; integration event -&gt;
/// <see cref="EventEnvelope"/>, in one place" shape <see cref="BookingPendingStateChangedMapper"/>
/// already establishes for a fact with no single domain event of its own to carry: a create and a
/// reconfigure are two different <see cref="WorkerSchedule"/> members, not one raised event this mapper
/// could take instead.
/// </summary>
public static class WorkerScheduleSavedMapper
{
    public static EventEnvelope ToEnvelope(
        CalendarId calendarId, WorkerId workerId, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);

        var correlationId = idGenerator.NewId(occurredAt);
        var contract = new WorkerScheduleSaved(calendarId.Value, workerId.Value, occurredAt, correlationId);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(WorkerScheduleSaved),
            Version: 1,
            // Per calendar - messaging.md guarantees order per partition key, never globally, and the
            // only ordering this event could ever need is between this one calendar's own saves.
            PartitionKey: calendarId.Value.ToString(),
            OccurredAt: occurredAt,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
