using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The storage-level no-overlap rule rejected a write: this worker already has an event covering
/// part of that interval.
///
/// <para><b>The reason this type exists is that the rule is not enforceable anywhere else.</b>
/// <see cref="Event"/> can only see itself, so "does some other row overlap" is a question about a
/// set - and answering it by reading first and writing second is a check-then-act, which two
/// concurrent materialisations or a double-submitted manual edit will walk straight through.
/// Postgres answers it atomically with a GiST exclusion constraint
/// (<c>ex_events_worker_no_overlap</c>). What is left for the application layer is naming the
/// failure: the adapter translates <c>23P01</c> into this, so a caller can distinguish "somebody
/// beat me to that time" from a broken query, without knowing that Npgsql or even Postgres is
/// involved.</para>
/// </summary>
public sealed class SlotOverlapException(WorkerId workerId, TimeSlot slot, Exception innerException)
    : Exception($"Worker {workerId.Value} already has an event overlapping {slot}.", innerException)
{
    public WorkerId WorkerId { get; } = workerId;

    public TimeSlot Slot { get; } = slot;
}
