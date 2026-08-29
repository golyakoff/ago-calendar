namespace Ago.Calendar.Domain;

/// <summary>
/// One visitor's walk through the booking flow when it is driven from a chat conversation
/// (`20-07`) rather than the widget calling <see cref="Event"/>'s own handlers directly page by page.
///
/// <para><b>Why this is a new aggregate rather than fields on <see cref="Event"/> or
/// <see cref="Customer"/>.</b> This is chat-orchestration state - which primitive was last sent and
/// what has been picked so far in <i>this</i> exchange - not booking-domain state. <see cref="Event"/>
/// already has a state machine with no "half-picked" status, and it should not grow one for a UI
/// concern: <c>Available -&gt; PendingConfirmation</c> only happens once the whole thing is decided,
/// through <see cref="BookingCalendar"/>'s own <c>IBookingStore.TryBookAsync</c> compare-and-set, and
/// that write is unaware this task ever existed. A row here can be abandoned mid-flow (a visitor who
/// never replies) and that must not touch a single <see cref="Event"/> or <see cref="Customer"/> row -
/// the two lifecycles are genuinely independent, which is the sharpest test for "does this belong on
/// the aggregate it is about" (clean-architecture.md).</para>
///
/// <para><b>Opaque to `Ago.Chat.*` on purpose, and opaque the other way too.</b> <c>externalTaskId</c>
/// on the wire is this aggregate's own <see cref="Id"/> in string form - nothing new to correlate,
/// because nothing but this aggregate mints it. The <c>chatTaskId</c>/<c>siteId</c>/<c>conversationId</c>
/// Chat's own requests carry are accepted by the Application handlers only long enough to be handed
/// back unread on the next call; they are never stored here, matching the wire contract's own promise
/// that this product understands none of them (adr/0065's guard 2, mirrored).</para>
///
/// <para><b>What this aggregate enforces.</b> Exactly one thing: a reply can only advance the step the
/// task is actually waiting on (<see cref="State"/>). It has no opinion on whether a chosen service id
/// or slot is real, offered, or still available - that is <see cref="Event"/>'s and the existing
/// `PublicBooking` handlers' job, asked again on every step, and it is what keeps a stale or tampered
/// <c>value</c> safe rather than something this aggregate has to defend against: nothing here can
/// point a downstream call at another tenant's data, because <see cref="TenantId"/> and
/// <see cref="CalendarId"/> come only from this deployment's static configuration, never from a
/// chat-supplied value.</para>
/// </summary>
public sealed class ChatBookingTask
{
    public ChatBookingTaskId Id { get; }

    public TenantId TenantId { get; }

    public CalendarId CalendarId { get; }

    public ServiceId? ServiceId { get; private set; }

    public WorkerId? WorkerId { get; private set; }

    public EventId? EventId { get; private set; }

    /// <summary>Raw, as typed by the visitor - the same "unvalidated until a handler turns it into a
    /// <see cref="PhoneNumber"/>" shape <see cref="BookingAttempt"/> already carries. Kept here for
    /// the record of what this task collected, not re-validated a second time.</summary>
    public string? Phone { get; private set; }

    public ChatBookingTaskState State { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private ChatBookingTask(
        ChatBookingTaskId id,
        TenantId tenantId,
        CalendarId calendarId,
        ChatBookingTaskState state,
        DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        CalendarId = calendarId;
        State = state;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private ChatBookingTask()
    {
    }

    /// <summary>A task begins already waiting on the first reply: the service list is sent in the
    /// same request that creates the row (`StartModuleTaskHandler`), so there is no earlier state to
    /// name.</summary>
    public static ChatBookingTask Start(
        ChatBookingTaskId id, TenantId tenantId, CalendarId calendarId, DateTimeOffset now) =>
        new(id, tenantId, calendarId, ChatBookingTaskState.AwaitingServiceChoice, now);

    public void ChooseService(ServiceId serviceId, DateTimeOffset now)
    {
        RequireState(ChatBookingTaskState.AwaitingServiceChoice);
        ServiceId = serviceId;
        State = ChatBookingTaskState.AwaitingWorkerChoice;
        UpdatedAt = now;
    }

    public void ChooseWorker(WorkerId workerId, DateTimeOffset now)
    {
        RequireState(ChatBookingTaskState.AwaitingWorkerChoice);
        WorkerId = workerId;
        State = ChatBookingTaskState.AwaitingSlotChoice;
        UpdatedAt = now;
    }

    public void ChooseSlot(EventId eventId, DateTimeOffset now)
    {
        RequireState(ChatBookingTaskState.AwaitingSlotChoice);
        EventId = eventId;
        State = ChatBookingTaskState.AwaitingPhone;
        UpdatedAt = now;
    }

    /// <summary>The booking attempt succeeded. Terminal - see <see cref="ChatBookingTaskState.Completed"/>.</summary>
    public void Complete(string phone, DateTimeOffset now)
    {
        RequireState(ChatBookingTaskState.AwaitingPhone);
        Phone = phone;
        State = ChatBookingTaskState.Completed;
        UpdatedAt = now;
    }

    /// <summary>
    /// The booking attempt lost - the slot <see cref="EventId"/> named was taken, blocked, or
    /// otherwise unclaimable by the time the phone number arrived. Not a dead end: the backlog item's
    /// own words are "the visitor should never see a dead end", so this returns the task to
    /// <see cref="ChatBookingTaskState.AwaitingSlotChoice"/> rather than failing it, and the caller
    /// re-queries <c>GetOpenSlotsHandler</c> for a fresh list before sending the next step. The chosen
    /// worker is kept - only the slot that just lost the race is cleared.
    /// </summary>
    public void ReopenForSlotChoice(string phone, DateTimeOffset now)
    {
        RequireState(ChatBookingTaskState.AwaitingPhone);
        Phone = phone;
        EventId = null;
        State = ChatBookingTaskState.AwaitingSlotChoice;
        UpdatedAt = now;
    }

    private void RequireState(ChatBookingTaskState expected)
    {
        if (State != expected)
        {
            throw new InvalidChatBookingTaskStateException(
                $"Cannot advance task {Id.Value} from state {State}; only a reply while it is " +
                $"{expected} may do this.");
        }
    }
}
