namespace Ago.Calendar.Domain;

/// <summary>
/// A half-open interval of *absolute* time, <c>[StartsAt, EndsAt)</c> - the only thing in this
/// domain that is an instant rather than a wall-clock reading.
///
/// <para><b>Why half-open, and why that is not a detail.</b> Back-to-back slots share a boundary:
/// 10:00-11:00 and 11:00-12:00 are adjacent, not overlapping. With a closed interval every adjacent
/// pair a materialisation produces would collide with its neighbour, and the storage-level
/// no-overlap constraint (<c>ex_events_worker_no_overlap</c>) would reject a perfectly ordinary
/// working day. That constraint is declared with the same <c>'[)'</c> bound, so it and
/// <see cref="Overlaps"/> answer identically - one guarantee stated twice, never two rules free to
/// drift apart.</para>
///
/// <para><b>Why <see cref="DateTimeOffset"/> and not a local date plus a time.</b> Two slots overlap
/// or they do not, and that question has one answer for everyone on earth. A wall-clock pair only
/// answers it once a zone is applied, and inside the hour a DST transition repeats it has no single
/// answer at all. Wall clock lives in <see cref="WorkingHoursRule"/>, which is a *recurrence*, and
/// becomes a <see cref="TimeSlot"/> exactly once - at materialisation (`20-02`), through the
/// calendar's <see cref="CalendarTimeZone"/>.</para>
/// </summary>
public readonly record struct TimeSlot
{
    public TimeSlot(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt), endsAt, $"A slot must end after it starts; got {startsAt:O} .. {endsAt:O}.");
        }

        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    public DateTimeOffset StartsAt { get; }

    public DateTimeOffset EndsAt { get; }

    public TimeSpan Duration => EndsAt - StartsAt;

    /// <summary>Half-open comparison - see the type's own remarks for why touching endpoints are not
    /// an overlap.</summary>
    public bool Overlaps(TimeSlot other) => StartsAt < other.EndsAt && other.StartsAt < EndsAt;

    public override string ToString() => $"{StartsAt:O}..{EndsAt:O}";
}
