namespace Ago.Calendar.Domain;

/// <summary>
/// The centre of this domain: one row that is both a free slot and, later, the booking that took it.
///
/// <para><b>Why one row with a status column rather than a "slot" table and a "booking" table.</b>
/// The question this product exists to answer correctly is "did two customers just take the same
/// slot?", and that is a concurrency question. With one row, the answer is a single
/// compare-and-set - <c>UPDATE events SET status = 'PendingConfirmation' ... WHERE id = @id AND
/// status = 'Available'</c> - whose rows-affected count *is* the verdict: one writer sees 1, the
/// other sees 0, and Postgres decides, not us. With a slot table and a separate bookings table, the
/// same question becomes "does a booking already exist for this slot?", which is a read followed by
/// an insert - a check-then-act across two statements that is wrong under concurrency unless a
/// second mechanism (a unique index on <c>slot_id</c>, or a lock) is bolted on to make it right. At
/// that point the unique index is doing the work and the second table is only paperwork. `6-09`/
/// `6-10` in AGO Chat are the worked example of the check-then-act version being the wrong answer,
/// and CLAUDE.md rule 8 states the rule this follows: a compare-and-set a write decision depends on
/// reads from the database, inside the transaction.</para>
///
/// <para><b>Where the overlap invariant lives: the database, not this aggregate.</b> Two *different*
/// events covering the same worker at the same time is the other half of "no double booking", and
/// this type cannot enforce it. An aggregate can only see itself; deciding whether some other row
/// overlaps would mean loading the worker's neighbouring events and checking - a check-then-act over
/// a set, which is exactly the shape that loses a race. So the rule is declared where it can be kept
/// atomically: a GiST exclusion constraint on <c>events</c>, <c>ex_events_worker_no_overlap</c>,
/// which rejects the second of two concurrent overlapping inserts with <c>23P01</c> no matter which
/// process, transaction or host issued it. <see cref="TimeSlot.Overlaps"/> answers the same question
/// in memory for callers that want to fail early and for tests; it is a convenience, never the
/// guarantee.</para>
///
/// <para><b>What this aggregate does enforce.</b> The state machine: which transition may follow
/// which, and the time-based preconditions each one has (a claim on a slot that already started, a
/// deadline in the past, a no-show on a visit that has not happened yet). Those are facts about this
/// row alone, which is precisely why they belong here and the overlap rule does not.</para>
///
/// <para><b>Status transitions.</b>
/// <c>Available -&gt; PendingConfirmation</c> (<see cref="Claim"/>),
/// <c>PendingConfirmation -&gt; Booked</c> (<see cref="Confirm"/>),
/// <c>PendingConfirmation -&gt; Cancelled</c> (<see cref="Reject"/>),
/// <c>PendingConfirmation | Booked -&gt; Cancelled</c> (<see cref="Cancel"/>),
/// <c>Booked -&gt; NoShow</c> (<see cref="MarkNoShow"/>),
/// <c>Available -&gt; Blocked</c> (<see cref="Block"/>). Everything else throws
/// <see cref="InvalidEventStateException"/> - including any route back to
/// <see cref="EventStatus.Available"/>, which no method offers: re-offering a slot whose claim was
/// vetoed is a product decision `20-04` has not made yet, and a transition built before the decision
/// would be a guess with a customer's booking attached to it.</para>
/// </summary>
public sealed class Event
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public EventId Id { get; }

    /// <summary>
    /// Carried on the row itself even though it is reachable through <see cref="CalendarId"/>. A
    /// deliberate denormalisation, and the precedent is a documented regret rather than a preference:
    /// AGO Chat's <c>messages</c> has no <c>site_id</c>, and data-model.md records the consequence -
    /// every per-tenant question about messages is a join, forever. This product's own headline read
    /// is `20-04`'s tenant-wide pending-confirmation queue ("everything waiting on any of my
    /// calendars"), so the join would be on the hottest path rather than a reporting one.
    /// </summary>
    public TenantId TenantId { get; }

    public CalendarId CalendarId { get; }

    public WorkerId WorkerId { get; }

    /// <summary>Absent until a customer claims the slot, and absent forever on a
    /// <see cref="EventStatus.Blocked"/> row - a closure is not a service.</summary>
    public ServiceId? ServiceId { get; private set; }

    /// <summary>Set by <see cref="Claim"/> and never cleared, including on cancellation: who
    /// cancelled on whom is exactly the history a lead card exists to keep.</summary>
    public CustomerId? CustomerId { get; private set; }

    /// <summary>Instant the slot opens. Mapped as its own <c>timestamptz</c> column; see
    /// <see cref="Slot"/>.</summary>
    public DateTimeOffset StartsAt { get; }

    /// <summary>Instant the slot closes, exclusive.</summary>
    public DateTimeOffset EndsAt { get; }

    /// <summary>
    /// The business-local calendar day this slot belongs to, in the owning calendar's
    /// <see cref="CalendarTimeZone"/>.
    ///
    /// <para><b>Stored, not derived, and that is the interesting decision.</b> "Which day is this?"
    /// is zone-dependent (date-and-time.md: "a day is zone-dependent"), so deriving it in SQL means
    /// every query that groups or filters by day must carry the zone and call
    /// <c>AT TIME ZONE</c> - non-sargable, so no index can serve it, and wrong the moment one query
    /// forgets the zone. Storing the answer the materialiser already computed makes "delete this
    /// worker's slots for Tuesday" (`20-02`'s direct editing, the feature that replaced declarative
    /// schedule exceptions) an indexed equality predicate instead. The cost is honest and worth
    /// stating: this column is only correct for the zone the calendar had when the row was written,
    /// which is exactly why <see cref="BookingCalendar.TimeZone"/> has no setter.</para>
    ///
    /// <para>Passed in rather than computed here: the conversion needs the tz database, and Domain
    /// must not read ambient machine state (CLAUDE.md rule 2, and see
    /// <see cref="CalendarTimeZone"/>).</para>
    /// </summary>
    public DateOnly LocalDate { get; }

    public EventStatus Status { get; private set; }

    /// <summary>
    /// When the operator veto window closes. Set by <see cref="Claim"/>, and the only thing `20-04`'s
    /// sweep needs to find rows to auto-confirm. Null in every status except
    /// <see cref="EventStatus.PendingConfirmation"/>.
    ///
    /// <para>An absolute instant, computed by the caller from <c>IClock</c>-supplied time plus
    /// the tenant's configured window. Not a duration stored on the row: a duration would have to be
    /// added to something at read time, and the only sane something is the claim time, which makes
    /// two columns out of one and gives a sweep query an expression to filter on instead of a plain
    /// <c>&lt;= now()</c>.</para>
    /// </summary>
    public DateTimeOffset? ConfirmationDeadline { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The slot as a value object. Computed from the two mapped columns rather than mapped itself:
    /// EF would need to materialise a validating struct through a constructor, and the row is
    /// trivially two <c>timestamptz</c>s. The invariant still holds where it matters - every factory
    /// below takes a <see cref="TimeSlot"/>, so an inverted interval cannot reach the constructor,
    /// let alone the table.
    /// </summary>
    public TimeSlot Slot => new(StartsAt, EndsAt);

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    private Event(
        EventId id, TenantId tenantId, CalendarId calendarId, WorkerId workerId,
        TimeSlot slot, DateOnly localDate, EventStatus status, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        CalendarId = calendarId;
        WorkerId = workerId;
        StartsAt = slot.StartsAt;
        EndsAt = slot.EndsAt;
        LocalDate = localDate;
        Status = status;
        CreatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private Event()
    {
    }

    /// <summary>
    /// A free slot, produced by `20-02` from a <see cref="WorkingHoursRule"/>.
    ///
    /// <para><b>Raises no domain event, unlike every transition below.</b> One materialisation run
    /// writes a horizon's worth of slots for every worker on a calendar - thousands of rows - and
    /// nothing downstream reacts to a slot merely existing. An event per row would be an outbox
    /// write per row for no consumer, which is the outbox's own worst case for no benefit.</para>
    /// </summary>
    public static Event Materialize(
        EventId id, TenantId tenantId, CalendarId calendarId, WorkerId workerId,
        TimeSlot slot, DateOnly localDate, DateTimeOffset now) =>
        new(id, tenantId, calendarId, workerId, slot, localDate, EventStatus.Available, now);

    /// <summary>Time the worker is unavailable, written directly as <see cref="EventStatus.Blocked"/>
    /// rather than materialised and then blocked - a lunch break was never bookable, and a row that
    /// is <c>Available</c> for an instant is a row somebody can claim in that instant. It still
    /// occupies the worker, so the no-overlap constraint covers it exactly like a booking.</summary>
    public static Event BlockOut(
        EventId id, TenantId tenantId, CalendarId calendarId, WorkerId workerId,
        TimeSlot slot, DateOnly localDate, DateTimeOffset now) =>
        new(id, tenantId, calendarId, workerId, slot, localDate, EventStatus.Blocked, now);

    /// <summary>
    /// A customer takes the slot. <c>Available -&gt; PendingConfirmation</c>.
    ///
    /// <para><b>This method is not what makes the claim safe.</b> Two callers can each load an
    /// <c>Available</c> copy of this row and both succeed here, in memory. What separates them is the
    /// storage-level compare-and-set (`20-03`) or, for a load-mutate-save through EF, the row's own
    /// <c>xmin</c>: one save commits and the other is rejected outright. The check below is what
    /// rejects a caller working from a copy it already knows is stale, and what makes the state
    /// machine readable - it is the first line of defence, not the guarantee (the same division
    /// concurrency.md draws for AGO Chat's message sequence).</para>
    /// </summary>
    /// <param name="confirmationDeadline">When the operator veto window closes - an absolute instant
    /// the caller computed from the tenant's configured window. Must be after
    /// <paramref name="now"/>: a window that has already closed would be auto-confirmed by the very
    /// next sweep tick, which is not a veto window at all.</param>
    public void Claim(
        CustomerId customerId, ServiceId serviceId, DateTimeOffset now, DateTimeOffset confirmationDeadline)
    {
        if (Status != EventStatus.Available)
        {
            throw new InvalidEventStateException(
                $"Cannot claim event {Id.Value} in state {Status}; only {EventStatus.Available} can be claimed.");
        }

        if (StartsAt <= now)
        {
            throw new InvalidEventStateException(
                $"Cannot claim event {Id.Value}: its slot starts at {StartsAt:O}, which is not in the future.");
        }

        if (confirmationDeadline <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmationDeadline), confirmationDeadline,
                "The confirmation window must close in the future.");
        }

        CustomerId = customerId;
        ServiceId = serviceId;
        ConfirmationDeadline = confirmationDeadline;
        Status = EventStatus.PendingConfirmation;
        _domainEvents.Add(new EventClaimed(
            Id, TenantId, CalendarId, WorkerId, serviceId, customerId, Slot, confirmationDeadline, now));
    }

    /// <summary>
    /// <c>PendingConfirmation -&gt; Booked</c>.
    ///
    /// <para>Deliberately does not check the deadline. Both real callers are legitimate on either
    /// side of it: `20-04`'s sweep confirms rows whose window has closed, and an operator who is
    /// looking at the request right now may confirm it immediately rather than wait. Enforcing
    /// "after the deadline only" here would forbid the second, and enforcing "before" would forbid
    /// the first - the timing rule belongs to whoever is calling, and there are two of them with
    /// opposite needs.</para>
    /// </summary>
    public void Confirm(DateTimeOffset now)
    {
        if (Status != EventStatus.PendingConfirmation)
        {
            throw new InvalidEventStateException(
                $"Cannot confirm event {Id.Value} in state {Status}; " +
                $"only {EventStatus.PendingConfirmation} can be confirmed.");
        }

        ConfirmationDeadline = null;
        Status = EventStatus.Booked;
        _domainEvents.Add(new EventConfirmed(Id, TenantId, CustomerId!.Value, Slot, now));
    }

    /// <summary>The operator's veto, inside the window. <c>PendingConfirmation -&gt; Cancelled</c>.
    /// The slot is not re-offered - see the type's own remarks on why no transition back to
    /// <see cref="EventStatus.Available"/> exists yet.</summary>
    public void Reject(DateTimeOffset now)
    {
        if (Status != EventStatus.PendingConfirmation)
        {
            throw new InvalidEventStateException(
                $"Cannot reject event {Id.Value} in state {Status}; " +
                $"only {EventStatus.PendingConfirmation} can be rejected.");
        }

        Withdraw(CancellationReason.RejectedByOperator, now);
    }

    /// <summary><c>PendingConfirmation | Booked -&gt; Cancelled</c>. v1 has exactly one caller: an
    /// operator, by hand, through the console - the product spec rules out customer-initiated
    /// cancellation and rescheduling outright.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (EventStatus.PendingConfirmation or EventStatus.Booked))
        {
            throw new InvalidEventStateException(
                $"Cannot cancel event {Id.Value} in state {Status}; " +
                $"only {EventStatus.PendingConfirmation} or {EventStatus.Booked} can be cancelled.");
        }

        Withdraw(CancellationReason.CancelledByOperator, now);
    }

    /// <summary>
    /// <c>Booked -&gt; NoShow</c>, and only for a visit that has already ended.
    ///
    /// <para>The time check is a real invariant, not defensiveness: a no-show is a statement about
    /// something that did not happen, and it cannot be made about a visit that has not had its
    /// chance yet. It is also the cheapest possible demonstration of why time is a parameter here -
    /// the rule is testable at any instant, in any zone, without waiting.</para>
    /// </summary>
    public void MarkNoShow(DateTimeOffset now)
    {
        if (Status != EventStatus.Booked)
        {
            throw new InvalidEventStateException(
                $"Cannot mark event {Id.Value} as a no-show in state {Status}; " +
                $"only {EventStatus.Booked} can be.");
        }

        if (now < EndsAt)
        {
            throw new InvalidEventStateException(
                $"Cannot mark event {Id.Value} as a no-show before its slot ends at {EndsAt:O}.");
        }

        Status = EventStatus.NoShow;
        _domainEvents.Add(new EventNoShowRecorded(Id, TenantId, CustomerId!.Value, Slot, now));
    }

    /// <summary>Takes a free slot out of circulation without a customer - `20-02`'s direct editing of
    /// an already materialised day. <c>Available -&gt; Blocked</c> only: blocking a claimed slot
    /// would silently strand a customer who was told to wait for a confirmation, so that path is a
    /// cancellation, which tells them.</summary>
    public void Block()
    {
        if (Status != EventStatus.Available)
        {
            throw new InvalidEventStateException(
                $"Cannot block event {Id.Value} in state {Status}; only {EventStatus.Available} can be blocked. " +
                "Cancel a claimed slot instead - the customer has to be told.");
        }

        Status = EventStatus.Blocked;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void Withdraw(CancellationReason reason, DateTimeOffset now)
    {
        ConfirmationDeadline = null;
        Status = EventStatus.Cancelled;
        _domainEvents.Add(new EventCancelled(Id, TenantId, CustomerId, Slot, reason, now));
    }
}
