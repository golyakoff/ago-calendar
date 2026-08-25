namespace Ago.Calendar.Domain;

/// <summary>
/// v1's deliberate ceiling: one worker participates in exactly one calendar
/// (<c>docs/backlog/20-01-domain-model-and-persistence.md</c>, and the product spec it plans
/// against).
///
/// <para><b>A limit, not an architectural boundary.</b> The relationship is already modelled as a
/// many-to-many join (<see cref="CalendarMembership"/>, table <c>calendar_workers</c>) precisely so
/// that widening it later is subtractive: delete the one check in <see cref="Worker.JoinCalendar"/>
/// that throws this, and the schema, the aggregate and <see cref="WorkingHoursRule"/>'s
/// <c>(worker, calendar)</c> scoping all already support a worker in several calendars.
/// clean-architecture.md's "promotion is cheap" argument, applied to loosening a rule rather than
/// tightening one - a direction that is only cheap when the shape underneath was built for it from
/// the start.</para>
/// </summary>
public sealed class WorkerCalendarLimitException(string message) : InvalidOperationException(message);
