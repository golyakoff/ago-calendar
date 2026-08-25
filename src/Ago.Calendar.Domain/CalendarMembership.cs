namespace Ago.Calendar.Domain;

/// <summary>One row of <c>calendar_workers</c>, owned by the <see cref="Worker"/> aggregate. A join
/// row and nothing more - no surrogate key, no behaviour, no invariants of its own; the invariants
/// live in <see cref="Worker.JoinCalendar"/>, which is the only thing that creates one.</summary>
public sealed class CalendarMembership
{
    public WorkerId WorkerId { get; private set; }

    public CalendarId CalendarId { get; private set; }

    internal CalendarMembership(WorkerId workerId, CalendarId calendarId)
    {
        WorkerId = workerId;
        CalendarId = calendarId;
    }

    // EF Core materialization only - never called by domain code.
    private CalendarMembership()
    {
    }
}
