namespace Ago.Calendar.Domain;

/// <summary>
/// A recurring weekday window - "Monday, 09:00 to 18:00" - scoped to the pair
/// <c>(<see cref="WorkerId"/>, <see cref="CalendarId"/>)</c>. The source `20-02` materialises
/// <see cref="Event"/> rows from.
///
/// <para><b>This is wall clock, and that is the whole point of the type.</b>
/// <see cref="StartsAt"/>/<see cref="EndsAt"/> are <see cref="TimeOnly"/> and the day is a
/// <see cref="DayOfWeek"/>, because "we open at nine" is a statement about a clock on a wall, not
/// about an instant. Storing it as a <see cref="DateTimeOffset"/> would force a single offset to be
/// chosen at configuration time, and that offset is wrong for half the year in any zone that
/// observes DST - the shop would open an hour early or an hour late, once every six months, for
/// everyone. The conversion happens exactly once, in one place, at materialisation, through
/// <see cref="BookingCalendar.TimeZone"/>: wall clock in, instants out. Nothing downstream ever
/// converts again, and nothing upstream ever stores an instant.</para>
///
/// <para><b>Deliberately not a general recurrence language.</b> No RRULE, no interval, no
/// exceptions. The product spec replaced schedule exceptions with direct editing of the already
/// materialised rows, so the rule only ever has to answer "what does an ordinary week look like from
/// here on" - and a rule that expresses less is a rule the materialiser cannot misinterpret.</para>
/// </summary>
public sealed class WorkingHoursRule
{
    public WorkingHoursRuleId Id { get; }

    public WorkerId WorkerId { get; }

    public CalendarId CalendarId { get; }

    public DayOfWeek DayOfWeek { get; private set; }

    /// <summary>Local wall-clock opening time in the calendar's zone - never an instant.</summary>
    public TimeOnly StartsAt { get; private set; }

    /// <summary>Local wall-clock closing time in the calendar's zone. Strictly after
    /// <see cref="StartsAt"/>, so a rule never wraps past midnight: a shift that crosses midnight is
    /// two rules on two days, which is also how a human would describe it.</summary>
    public TimeOnly EndsAt { get; private set; }

    private WorkingHoursRule(
        WorkingHoursRuleId id, WorkerId workerId, CalendarId calendarId,
        DayOfWeek dayOfWeek, TimeOnly startsAt, TimeOnly endsAt)
    {
        Id = id;
        WorkerId = workerId;
        CalendarId = calendarId;
        DayOfWeek = dayOfWeek;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    // EF Core materialization only - never called by domain code.
    private WorkingHoursRule()
    {
    }

    /// <summary>
    /// Takes the <see cref="Worker"/> and the <see cref="BookingCalendar"/> themselves, not their
    /// ids: this factory is where v1's one-calendar-per-worker limit is actually enforceable, since
    /// only the worker knows which calendar it belongs to. Building a rule for a calendar the worker
    /// does not work in is rejected here rather than discovered later by a materialiser that finds
    /// hours for a worker who is not on the calendar it is generating.
    /// </summary>
    public static WorkingHoursRule For(
        WorkingHoursRuleId id, Worker worker, BookingCalendar calendar,
        DayOfWeek dayOfWeek, TimeOnly startsAt, TimeOnly endsAt)
    {
        Validate(worker, calendar, startsAt, endsAt);
        return new WorkingHoursRule(id, worker.Id, calendar.Id, dayOfWeek, startsAt, endsAt);
    }

    /// <summary>
    /// `26-97`: corrects an existing rule in place - the same three questions <see cref="For"/> asks,
    /// asked again, through the same <see cref="Validate"/> call rather than a second copy of them.
    /// That is the whole reason this method takes the <see cref="Worker"/> and the
    /// <see cref="BookingCalendar"/> instead of nothing at all: a correction that skipped the tenant
    /// and membership checks would be a door into this aggregate that creation does not have, and
    /// "refusing wherever the domain already refuses a new rule" is only true if it is literally the
    /// same refusal.
    ///
    /// <para><b>Which fields may move, and which may not.</b> The weekday and the two wall-clock
    /// times - the three a human types and can mistype. Never <see cref="WorkerId"/> or
    /// <see cref="CalendarId"/>: moving a rule to another worker is indistinguishable from deleting
    /// it and adding one, and moving it to another calendar is refused one layer down anyway, since
    /// v1 gives a worker exactly one calendar (<see cref="Worker.JoinCalendar"/>). Identity that
    /// cannot move is what lets the caller keep treating this row as the same rule.</para>
    ///
    /// <para><b>What this deliberately does not do: touch a single already-materialised
    /// <see cref="Event"/>.</b> A rule is the materialiser's *input*, and the materialiser
    /// (<c>MaterializeAvailabilityHandler</c>) only ever inserts into days that have no row at all
    /// and only ever forward of <c>WorkerSchedule.MaterializeFrom</c> - so days already cut from the
    /// old hours keep the grid they were cut with, bookings included, and no edit here can destroy
    /// one. Re-cutting those days is `20-16`'s own destructive, human-confirmed flow
    /// (<c>WorkerSchedule.RecutFrom</c>), and the Application layer's job on this path is to say so
    /// out loud rather than let the operator assume the correction reached days it did not. See
    /// <c>WorkingHoursReconciler</c>.</para>
    /// </summary>
    public void ChangeTo(Worker worker, BookingCalendar calendar, DayOfWeek dayOfWeek, TimeOnly startsAt, TimeOnly endsAt)
    {
        Validate(worker, calendar, startsAt, endsAt);

        if (worker.Id != WorkerId || calendar.Id != CalendarId)
        {
            throw new WorkerCalendarLimitException(
                $"Rule {Id.Value} belongs to worker {WorkerId.Value} on calendar {CalendarId.Value}; " +
                "a rule is corrected in place, never moved to another worker or calendar.");
        }

        DayOfWeek = dayOfWeek;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    /// <summary>The three refusals, in one place so <see cref="For"/> and <see cref="ChangeTo"/>
    /// cannot drift apart - the failure mode being an edit path that accepts what creation
    /// rejects.</summary>
    private static void Validate(Worker worker, BookingCalendar calendar, TimeOnly startsAt, TimeOnly endsAt)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(calendar);

        if (worker.TenantId != calendar.TenantId)
        {
            throw new TenantMismatchException(
                $"Worker {worker.Id.Value} and calendar {calendar.Id.Value} belong to different tenants.");
        }

        if (!worker.WorksIn(calendar.Id))
        {
            throw new WorkerCalendarLimitException(
                $"Worker {worker.Id.Value} does not participate in calendar {calendar.Id.Value}; " +
                "join the calendar before giving the worker hours in it.");
        }

        if (endsAt <= startsAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt), endsAt,
                $"Working hours must end after they start; got {startsAt:HH\\:mm} .. {endsAt:HH\\:mm}. " +
                "A shift crossing midnight is two rules, on two days.");
        }
    }
}
