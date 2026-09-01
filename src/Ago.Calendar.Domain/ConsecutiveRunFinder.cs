namespace Ago.Calendar.Domain;

/// <summary>
/// `20-18`: a service longer than one slot becomes several consecutive slots claimed as one booking.
/// This type answers the two questions that decision needs, and answers them the same way whether the
/// caller is about to claim a run or only listing one as a courtesy: how many slots a service needs
/// given a worker's own grid (<see cref="ComputeSlotsNeeded"/>), and whether a specific run of that
/// many slots actually exists, unbroken, starting at a chosen slot (<see cref="FindRun"/>).
///
/// <para><b>Why this lives in Domain, not Application.</b> Both questions are answered purely from
/// facts already in hand - a worker's <see cref="WorkerSchedule"/> numbers and a day's own
/// <see cref="Event"/> rows - with no port, no I/O and no ambient state. clean-architecture.md's own
/// test for "does this belong on the aggregate, or beside it" is whether the logic can be expressed
/// without reaching outside the aggregates it already has; this can, so putting it in Application
/// would only add a dependency direction with nothing to justify it - the alternative (a "domain
/// service" class living in Application because it spans two aggregate types) is a pattern this
/// codebase has not needed yet and does not need here either. Domain being able to answer "is this
/// evening even physically bookable" without a database is also what lets this type's own test suite
/// run in microseconds with no Postgres, the same bar <see cref="Event"/>'s own state machine tests
/// are held to.</para>
///
/// <para><b>"Consecutive" is computed here and nowhere else that matters.</b> The backlog item is
/// explicit that a client naming three arbitrary event ids must not be able to claim three unrelated
/// times as one booking - <see cref="FindRun"/> is the one function server-side code calls to decide
/// which ids form a legal run, and every caller (the claim itself, and the public listing's own
/// courtesy filter) uses it rather than re-deriving the rule.</para>
/// </summary>
public static class ConsecutiveRunFinder
{
    /// <summary>
    /// How many of a worker's own slots a service needs, given whether this worker's buffers count
    /// toward the service duration (<see cref="WorkerSchedule.BuffersCountTowardServiceDuration"/>).
    ///
    /// <para><b>The arithmetic, restated from the item's own worked example (70/30/10).</b> A run of
    /// <c>N</c> slots spans <c>N*slot + (N-1)*buffer</c> minutes end to end - the buffers between the
    /// run's own slots are always physically consumed by it (the item's own "the buffers inside a run
    /// belong to the booking"), whether or not they count toward satisfying the service's duration.
    /// What the tenant's own setting decides is which quantity has to reach the service's duration:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Counting the buffers</b> (the default): capacity is the run's whole span,
    ///   <c>N*slot + (N-1)*buffer</c>. Smallest <c>N</c> with
    ///   <c>N*slot + (N-1)*buffer &gt;= duration</c>, solved as
    ///   <c>N &gt;= (duration + buffer) / (slot + buffer)</c> - two slots for 70/30/10, ending 13:10.</item>
    ///   <item><b>Not counting them</b>: capacity is only the slot time, <c>N*slot</c>. Smallest
    ///   <c>N</c> with <c>N*slot &gt;= duration</c> - three slots for 70/30/10, ending 13:50, at the
    ///   cost of 40 minutes nobody asked the worker to hold.</item>
    /// </list>
    /// <para>Both are integer ceiling divisions, computed as <c>(a + b - 1) / b</c> rather than through
    /// floating point - a service duration is stored as whole minutes and so are
    /// <see cref="WorkerSchedule.SlotMinutes"/> and <see cref="WorkerSchedule.BufferMinutes"/>, so the
    /// exact integer form is available and a `double` would only risk a rounding surprise for no
    /// benefit.</para>
    /// </summary>
    public static int ComputeSlotsNeeded(
        int serviceDurationMinutes, int slotMinutes, int bufferMinutes, bool buffersCountTowardServiceDuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceDurationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferMinutes);

        if (buffersCountTowardServiceDuration)
        {
            var denominator = slotMinutes + bufferMinutes;
            var numerator = serviceDurationMinutes + bufferMinutes;
            return CeilingDivide(numerator, denominator);
        }

        return CeilingDivide(serviceDurationMinutes, slotMinutes);
    }

    /// <summary>
    /// Walks <paramref name="dayEvents"/> - one worker's whole business-local day, whatever each row's
    /// status - in start order, from <paramref name="startEventId"/>, and returns the ordered ids of
    /// the run that would satisfy <paramref name="serviceDurationMinutes"/>, or <see langword="null"/>
    /// when no such run exists starting there.
    ///
    /// <para><b>Every reason this can return null is a real one, not an edge case to special-case
    /// away.</b> The starting row is not in <paramref name="dayEvents"/> at all (a stale or foreign
    /// id); it is not <see cref="EventStatus.Available"/> (already taken, blocked, or the anchor of
    /// somebody else's booking); there are not enough rows left in the day after it; or a needed
    /// successor is not <see cref="EventStatus.Available"/>, or does not begin exactly
    /// <paramref name="bufferMinutes"/> after the previous one ends - a gap in the grid (a day
    /// boundary edit, a block, or simply the day ending) breaks the run exactly where a real customer
    /// would find the shop's diary broken too.</para>
    ///
    /// <para><b>Exact equality on the successor's start, not "close enough".</b> Every slot on one
    /// already-materialised day comes from one <see cref="WorkerSchedule"/> snapshot applied
    /// uniformly across the day (`20-02`'s materialiser generates a day's grid from one configuration
    /// in one run), so <c>previous.EndsAt + buffer</c> is not an estimate - it is the exact instant the
    /// grid's own next slot must begin if that slot exists at all. A day whose grid changed
    /// mid-generation (an edit landing between two materialisation runs, or a schedule change taking
    /// effect only from the cursor forward) is not a case this walk needs to tolerate: it would show
    /// up as the same "no exact successor" outcome any other broken run does, which is the correct,
    /// conservative answer - offer nothing rather than guess.</para>
    /// </summary>
    public static IReadOnlyList<EventId>? FindRun(
        IReadOnlyList<Event> dayEvents,
        EventId startEventId,
        int serviceDurationMinutes,
        int slotMinutes,
        int bufferMinutes,
        bool buffersCountTowardServiceDuration)
    {
        ArgumentNullException.ThrowIfNull(dayEvents);

        var slotsNeeded = ComputeSlotsNeeded(
            serviceDurationMinutes, slotMinutes, bufferMinutes, buffersCountTowardServiceDuration);

        var ordered = dayEvents.OrderBy(e => e.StartsAt).ToList();
        var startIndex = ordered.FindIndex(e => e.Id == startEventId);
        if (startIndex < 0)
        {
            return null;
        }

        var start = ordered[startIndex];
        if (start.Status != EventStatus.Available)
        {
            return null;
        }

        var run = new List<EventId>(slotsNeeded) { start.Id };
        var previous = start;

        for (var i = 1; i < slotsNeeded; i++)
        {
            var nextIndex = startIndex + i;
            if (nextIndex >= ordered.Count)
            {
                return null;
            }

            var candidate = ordered[nextIndex];
            if (candidate.Status != EventStatus.Available)
            {
                return null;
            }

            var expectedStart = previous.EndsAt.AddMinutes(bufferMinutes);
            if (candidate.StartsAt != expectedStart)
            {
                return null;
            }

            run.Add(candidate.Id);
            previous = candidate;
        }

        return run;
    }

    private static int CeilingDivide(int numerator, int denominator) => (numerator + denominator - 1) / denominator;
}
