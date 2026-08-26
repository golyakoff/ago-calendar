using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.MaterializeAvailability;

/// <summary>
/// Generates the <see cref="EventStatus.Available"/> rows a customer will later claim, out to a
/// rolling horizon, for every active worker on one calendar.
///
/// <para><b>Why rows exist before anybody books them.</b> The rejected alternative is to compute
/// availability on the fly - take the worker's rules, subtract the bookings, hand the customer the
/// gaps. It loses on the one question this product must answer correctly: *is this slot still
/// free?* A computed gap is not a thing, so "take it" cannot be a compare-and-set against anything;
/// it needs a lock invented over an interval, and two customers who both computed the same gap both
/// pass every check an application layer can make. A materialised row turns the whole race into
/// <c>UPDATE ... WHERE id = @id AND status = 'Available'</c>, whose rows-affected count is the
/// verdict and whose arbiter is Postgres. data-model.md already made this exact call once, for the
/// same reason, when it made AGO Chat's <c>active_chats</c> a real column instead of a count over
/// assignments. The second win is the feature this item is named after: with rows, "I am closed next
/// Tuesday" is an edit to Tuesday, not a declarative exception language bolted onto the recurrence
/// rule.</para>
///
/// <para><b>The non-destructive rule, stated once and enforced twice.</b> <i>This handler only ever
/// inserts, and only into business-local days that have no event row at all.</i> It never updates,
/// never deletes, and never regenerates a day it has already generated - so a slot that has moved on
/// to <c>PendingConfirmation</c>, <c>Booked</c>, <c>Cancelled</c> or <c>NoShow</c>, and a day a
/// tenant has edited by hand, are both untouchable by construction rather than by a check somebody
/// has to remember. The day-set query below is the cheap half of that; the exclusion constraint on
/// <c>events</c> is the half that holds when two <c>Ago.Calendar.Worker</c> replicas run this at the
/// same instant and both see the same empty day (adr/0049, adr/0053).</para>
///
/// <para><b>Where wall clock becomes an instant.</b> Exactly one call, on the line that resolves a
/// rule's <c>09:00 .. 18:00</c> against <see cref="BookingCalendar.TimeZone"/>, plus the one that
/// asks which local day "now" falls on. Everything after that - <see cref="SlotGrid"/>, the buffer
/// arithmetic, the "has this slot already ended" filter, the constraint - is absolute time. That is
/// the entire DST story: a rule at 09:00 local is 09:00 local on both sides of a transition because
/// each day is resolved on its own, and the day the clocks move is simply a shorter or longer window
/// than its neighbours.</para>
/// </summary>
public sealed class MaterializeAvailabilityHandler(
    IBookingCalendarRepository calendars,
    IWorkerRepository workers,
    IWorkingHoursRuleRepository rules,
    IServiceRepository services,
    IEventRepository events,
    IWallClockResolver wallClock,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<AvailabilityMaterialized> HandleAsync(
        MaterializeAvailability command, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(command.HorizonDays);

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        if (calendar is null)
        {
            // A calendar deleted between the job listing it and this handler loading it. Not an
            // error: the job's next tick simply will not list it.
            return AvailabilityMaterialized.Nothing;
        }

        var now = clock.UtcNow;

        // Conversion #1 of 2. "Today" is a question about a place, not about UTC - 21:00 in New York
        // is already tomorrow in UTC, so a horizon counted from the UTC date would be a day out for
        // every calendar west of Greenwich for part of every day.
        var firstDay = wallClock.ToLocalDate(calendar.TimeZone, now);
        var lastDay = firstDay.AddDays(command.HorizonDays);

        var calendarWorkers = await workers.ListActiveForCalendarAsync(calendar.Id, cancellationToken);
        if (calendarWorkers.Count == 0)
        {
            return AvailabilityMaterialized.Nothing;
        }

        var allRules = await rules.ListForCalendarAsync(calendar.Id, cancellationToken);
        var durations = await LoadServiceDurationsAsync(calendar.TenantId, cancellationToken);
        var buffer = TimeSpan.FromMinutes(calendar.BufferMinutes);

        var daysConsidered = 0;
        var daysSkipped = 0;
        var slotsInserted = 0;

        foreach (var worker in calendarWorkers)
        {
            var slotLength = SlotLengthFor(worker, durations);
            if (slotLength is null)
            {
                // A worker who performs no service has nothing bookable, so there is nothing to
                // generate. Silently producing zero-length or guessed-length slots would be worse
                // than producing none: a customer would see time they cannot actually book.
                continue;
            }

            var workerRules = allRules.Where(rule => rule.WorkerId == worker.Id).ToList();
            if (workerRules.Count == 0)
            {
                continue;
            }

            var alreadyMaterialized = await events.ListMaterializedLocalDatesAsync(
                calendar.Id, worker.Id, firstDay, lastDay, cancellationToken);

            var generated = new List<Event>();
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                daysConsidered++;

                // THE non-destructive rule, in one line: a day that already has any event row -
                // available, booked, blocked by a tenant's day-off, or cancelled - is never
                // regenerated. Everything this item promises about bookings surviving a repeated run
                // and manual edits surviving the next job tick reduces to this predicate.
                if (alreadyMaterialized.Contains(day))
                {
                    daysSkipped++;
                    continue;
                }

                generated.AddRange(GenerateDay(calendar, worker, day, workerRules, slotLength.Value, buffer, now));
            }

            slotsInserted += await events.InsertAvailableSlotsAsync(generated, cancellationToken);
        }

        return new AvailabilityMaterialized(daysConsidered, daysSkipped, slotsInserted);
    }

    private IEnumerable<Event> GenerateDay(
        BookingCalendar calendar,
        Worker worker,
        DateOnly day,
        IReadOnlyList<WorkingHoursRule> workerRules,
        TimeSpan slotLength,
        TimeSpan buffer,
        DateTimeOffset now)
    {
        foreach (var rule in workerRules.Where(rule => rule.DayOfWeek == day.DayOfWeek))
        {
            // Conversion #2 of 2, and the only one that turns a WorkingHoursRule into real time.
            // Resolved per day, never once per week: that is precisely what makes 09:00 mean 09:00
            // on both sides of a DST transition instead of drifting by an hour for half the year.
            var window = wallClock.ToInstantWindow(calendar.TimeZone, day, rule.StartsAt, rule.EndsAt);
            if (window is null)
            {
                continue;
            }

            foreach (var slot in SlotGrid.Fill(window.Value, slotLength, buffer))
            {
                // A first run at three in the afternoon must not publish this morning's slots.
                // Event.Claim would refuse them anyway, so they would be inert rows a customer can
                // see and cannot book - which is worse than an absence.
                if (slot.EndsAt <= now)
                {
                    continue;
                }

                yield return Event.Materialize(
                    new EventId(idGenerator.NewId(now)),
                    calendar.TenantId,
                    calendar.Id,
                    worker.Id,
                    slot,
                    day,
                    now);
            }
        }
    }

    private async Task<Dictionary<ServiceId, TimeSpan>> LoadServiceDurationsAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        var tenantServices = await services.ListForTenantAsync(tenantId, cancellationToken);
        return tenantServices.ToDictionary(service => service.Id, service => service.Duration);
    }

    /// <summary>
    /// How long one slot is: the <b>longest</b> service this worker offers.
    ///
    /// <para>A materialised slot exists before anybody has chosen a service for it -
    /// <see cref="Event.ServiceId"/> is null until <see cref="Event.Claim"/> - so its length has to
    /// be one that every service the worker performs fits inside. The shortest would have been the
    /// other candidate and is simply wrong: it would publish 15-minute slots that a 45-minute
    /// haircut can never be booked into, so the worker's main service would be unbookable. The cost
    /// of the longest is honest and stated rather than hidden: a worker offering a 15-minute and a
    /// 90-minute service publishes 90-minute slots, so a short booking consumes a long one. The real
    /// fix is a per-service grid, which is a different data model (a slot would stop being one row
    /// with one status), and it is not what this item is for.</para>
    /// </summary>
    private static TimeSpan? SlotLengthFor(Worker worker, Dictionary<ServiceId, TimeSpan> durations)
    {
        TimeSpan? longest = null;
        foreach (var offering in worker.Services)
        {
            if (durations.TryGetValue(offering.ServiceId, out var duration) && (longest is null || duration > longest))
            {
                longest = duration;
            }
        }

        return longest;
    }
}
