namespace Ago.Calendar.Domain.Tests;

public class ChatBookingTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private static readonly CalendarId CalendarId = new(Guid.NewGuid());
    private static readonly ServiceId ServiceId = new(Guid.NewGuid());
    private static readonly WorkerId WorkerId = new(Guid.NewGuid());
    private static readonly DateOnly SelectedDate = new(2026, 5, 11);
    private static readonly EventId EventId = new(Guid.NewGuid());

    [Fact]
    public void Start_BeginsAwaitingAServiceChoice()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        Assert.Equal(ChatBookingTaskState.AwaitingServiceChoice, task.State);
        Assert.Null(task.ServiceId);
        Assert.Equal(Now, task.CreatedAt);
        Assert.Equal(Now, task.UpdatedAt);
    }

    [Fact]
    public void TheHappyPath_AdvancesThroughEveryStateInOrder()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        task.ChooseService(ServiceId, Now);
        Assert.Equal(ChatBookingTaskState.AwaitingWorkerChoice, task.State);
        Assert.Equal(ServiceId, task.ServiceId);

        task.ChooseWorker(WorkerId, Now);
        // `25-33`: the date round, not the flat slot list directly.
        Assert.Equal(ChatBookingTaskState.AwaitingDateChoice, task.State);
        Assert.Equal(WorkerId, task.WorkerId);

        task.ChooseDate(SelectedDate, Now);
        Assert.Equal(ChatBookingTaskState.AwaitingSlotChoice, task.State);
        Assert.Equal(SelectedDate, task.SelectedDate);

        task.ChooseSlot(EventId, Now);
        Assert.Equal(ChatBookingTaskState.AwaitingPhone, task.State);
        Assert.Equal(EventId, task.EventId);

        task.Complete("+79990000001", Now);
        Assert.Equal(ChatBookingTaskState.Completed, task.State);
        Assert.Equal("+79990000001", task.Phone);
    }

    /// <summary>`25-33`: <see cref="ChatBookingTask.ChooseDate"/>'s own remarks on why
    /// <see cref="ChatBookingTask.LastAppliedValue"/> is set to the ISO date string - the exact
    /// format <c>ReplyToModuleTaskHandler</c> parses a date-round reply from and
    /// <c>ModuleStepFactory</c> encodes it as on the wire.</summary>
    [Fact]
    public void ChooseDate_SetsLastAppliedValueToTheIsoDateString()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);
        task.ChooseWorker(WorkerId, Now);

        task.ChooseDate(SelectedDate, Now);

        Assert.Equal("2026-05-11", task.LastAppliedValue);
    }

    /// <summary>`26-322`: an auto-selected sole service advances the state exactly as
    /// <see cref="ChatBookingTask.ChooseService"/> does, but deliberately leaves
    /// <see cref="ChatBookingTask.LastAppliedValue"/> untouched - no client reply chose it, so there is
    /// nothing for a retry to replay against, and (from Start) it stays null.</summary>
    [Fact]
    public void AutoChooseService_AdvancesToWorkerChoice_WithoutSettingLastAppliedValue()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        task.AutoChooseService(ServiceId, Now);

        Assert.Equal(ChatBookingTaskState.AwaitingWorkerChoice, task.State);
        Assert.Equal(ServiceId, task.ServiceId);
        Assert.Null(task.LastAppliedValue);
    }

    /// <summary>`26-322`: an auto-selected sole worker advances to the date round but keeps
    /// <see cref="ChatBookingTask.LastAppliedValue"/> as whatever the client's own reply set it to - the
    /// service id here, from the <see cref="ChatBookingTask.ChooseService"/> reply that triggered the
    /// skip. This is the property the replay guard relies on across the skip.</summary>
    [Fact]
    public void AutoChooseWorker_AfterAServiceReply_AdvancesToDateChoice_PreservingTheServiceReplyValue()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);

        task.AutoChooseWorker(WorkerId, Now);

        Assert.Equal(ChatBookingTaskState.AwaitingDateChoice, task.State);
        Assert.Equal(WorkerId, task.WorkerId);
        Assert.Equal(ServiceId.Value.ToString(), task.LastAppliedValue);
    }

    /// <summary>`26-322`: from Start (a one-service, one-worker calendar), both steps auto-skip before any
    /// reply, so <see cref="ChatBookingTask.LastAppliedValue"/> is still null when the task opens on the
    /// date round - there is no earlier client reply to have set it.</summary>
    [Fact]
    public void AutoChooseServiceThenAutoChooseWorker_FromStart_LeavesLastAppliedValueNull()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        task.AutoChooseService(ServiceId, Now);
        task.AutoChooseWorker(WorkerId, Now);

        Assert.Equal(ChatBookingTaskState.AwaitingDateChoice, task.State);
        Assert.Equal(ServiceId, task.ServiceId);
        Assert.Equal(WorkerId, task.WorkerId);
        Assert.Null(task.LastAppliedValue);
    }

    /// <summary>`26-322`: the auto-skip affordances guard their own required state, exactly as their
    /// explicit siblings do - auto-choosing a worker before a service is a caller bug, not an ordinary
    /// outcome.</summary>
    [Fact]
    public void AutoChoose_OutOfOrder_ThrowsRatherThanSilentlyAdvancing()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        // Still AwaitingServiceChoice - a worker cannot be auto-chosen yet.
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.AutoChooseWorker(WorkerId, Now));

        task.AutoChooseService(ServiceId, Now);
        // Now AwaitingWorkerChoice - the service cannot be auto-chosen a second time.
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.AutoChooseService(ServiceId, Now));
    }

    [Fact]
    public void ReopenForSlotChoice_ClearsTheLostSlot_ButKeepsTheChosenWorkerAndDate()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);
        task.ChooseWorker(WorkerId, Now);
        task.ChooseDate(SelectedDate, Now);
        task.ChooseSlot(EventId, Now);

        task.ReopenForSlotChoice("+79990000002", Now);

        Assert.Equal(ChatBookingTaskState.AwaitingSlotChoice, task.State);
        Assert.Null(task.EventId);
        Assert.Equal(WorkerId, task.WorkerId);
        // `25-33`: the date survives a lost race - the visitor is re-offered the *same* date's
        // remaining slots, not the date round again.
        Assert.Equal(SelectedDate, task.SelectedDate);
        Assert.Equal("+79990000002", task.Phone);
    }

    [Fact]
    public void AStepOutOfOrder_ThrowsRatherThanSilentlyAdvancing()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        // Still AwaitingServiceChoice - choosing a worker now would be a caller bug, not an
        // ordinary outcome, so it throws (coding-style.md) rather than moving the state machine.
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ChooseWorker(WorkerId, Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ChooseDate(SelectedDate, Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ChooseSlot(EventId, Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.Complete("+79990000003", Now));
    }

    [Fact]
    public void ACompletedTask_AcceptsNoFurtherTransition()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);
        task.ChooseWorker(WorkerId, Now);
        task.ChooseDate(SelectedDate, Now);
        task.ChooseSlot(EventId, Now);
        task.Complete("+79990000004", Now);

        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.Complete("+79990000004", Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ReopenForSlotChoice("+79990000004", Now));
    }

    [Fact]
    public void UpdatedAt_MovesWithEveryTransition()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        var later = Now.AddMinutes(3);

        task.ChooseService(ServiceId, later);

        Assert.Equal(Now, task.CreatedAt);
        Assert.Equal(later, task.UpdatedAt);
    }
}
