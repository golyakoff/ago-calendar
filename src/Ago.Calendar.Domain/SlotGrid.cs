namespace Ago.Calendar.Domain;

/// <summary>
/// Divides one already-resolved working window into the back-to-back slots a worker's day is made
/// of, separated by the calendar's buffer.
///
/// <para><b>This type takes instants, not wall clock, and that is the whole reason it can live in
/// Domain.</b> Turning "Tuesday, 09:00-18:00" into a pair of instants needs the tz database, which
/// is ambient machine state Domain must not read (CLAUDE.md rule 2, and see
/// <see cref="CalendarTimeZone"/>). That conversion happens once, in Infrastructure, before this
/// function is called - so everything below is plain arithmetic on absolute time, with no zone, no
/// clock and no ambiguity left in it. The alternative - a <c>SlotGrid</c> that took a
/// <see cref="WorkingHoursRule"/> and a zone id - would have dragged
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c> into the innermost layer and made this arithmetic
/// untestable without the host's own tz data.</para>
///
/// <para><b>Why the DST correctness falls out of that split rather than being handled here.</b> A
/// window is a span of real time. On the morning the clocks spring forward, a 06:00-12:00 wall-clock
/// window is five real hours, not six, so this function simply produces one fewer slot - it never
/// has to know why. On the morning they fall back the same window is seven hours and produces one
/// more. No slot can ever start at a local time that did not happen, because no local time is ever
/// mentioned below.</para>
///
/// <para><b>Half-open, and stepping by <c>slotLength + buffer</c>.</b> Slot <c>n</c> ends exactly
/// where slot <c>n+1</c>'s buffer begins, so with a zero buffer consecutive slots are adjacent and
/// the storage-level no-overlap constraint accepts them - <see cref="TimeSlot"/>'s own remarks
/// explain why a closed interval would reject an ordinary working day at its first boundary.</para>
/// </summary>
public static class SlotGrid
{
    /// <summary>
    /// The slots that fit inside <paramref name="window"/>. A partial slot at the end is not
    /// produced: a worker whose day is ten minutes short of another haircut does not have another
    /// haircut, and a rounded-up slot would be a booking the worker cannot honour.
    /// </summary>
    /// <param name="window">The working window as absolute time, already resolved through the
    /// calendar's zone.</param>
    /// <param name="slotLength">How long one bookable slot is.</param>
    /// <param name="buffer">Dead time between consecutive slots -
    /// <see cref="BookingCalendar.BufferMinutes"/>. Zero is legal and means back-to-back.</param>
    public static IReadOnlyList<TimeSlot> Fill(TimeSlot window, TimeSpan slotLength, TimeSpan buffer)
    {
        if (slotLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotLength), slotLength, "A slot must take a positive amount of time.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(buffer, TimeSpan.Zero, nameof(buffer));

        var slots = new List<TimeSlot>();

        // `stride` is strictly positive because slotLength is, which is what makes this loop
        // terminate for every input rather than for the inputs anybody happened to try.
        var stride = slotLength + buffer;

        for (var startsAt = window.StartsAt; startsAt + slotLength <= window.EndsAt; startsAt += stride)
        {
            slots.Add(new TimeSlot(startsAt, startsAt + slotLength));
        }

        return slots;
    }
}
