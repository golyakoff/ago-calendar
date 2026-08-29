namespace Ago.Calendar.Domain.Tests;

public class ChatBookingTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private static readonly CalendarId CalendarId = new(Guid.NewGuid());
    private static readonly ServiceId ServiceId = new(Guid.NewGuid());
    private static readonly WorkerId WorkerId = new(Guid.NewGuid());
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
        Assert.Equal(ChatBookingTaskState.AwaitingSlotChoice, task.State);
        Assert.Equal(WorkerId, task.WorkerId);

        task.ChooseSlot(EventId, Now);
        Assert.Equal(ChatBookingTaskState.AwaitingPhone, task.State);
        Assert.Equal(EventId, task.EventId);

        task.Complete("+79990000001", Now);
        Assert.Equal(ChatBookingTaskState.Completed, task.State);
        Assert.Equal("+79990000001", task.Phone);
    }

    [Fact]
    public void ReopenForSlotChoice_ClearsTheLostSlot_ButKeepsTheChosenWorker()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);
        task.ChooseWorker(WorkerId, Now);
        task.ChooseSlot(EventId, Now);

        task.ReopenForSlotChoice("+79990000002", Now);

        Assert.Equal(ChatBookingTaskState.AwaitingSlotChoice, task.State);
        Assert.Null(task.EventId);
        Assert.Equal(WorkerId, task.WorkerId);
        Assert.Equal("+79990000002", task.Phone);
    }

    [Fact]
    public void AStepOutOfOrder_ThrowsRatherThanSilentlyAdvancing()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);

        // Still AwaitingServiceChoice - choosing a worker now would be a caller bug, not an
        // ordinary outcome, so it throws (coding-style.md) rather than moving the state machine.
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ChooseWorker(WorkerId, Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.ChooseSlot(EventId, Now));
        Assert.Throws<InvalidChatBookingTaskStateException>(() => task.Complete("+79990000003", Now));
    }

    [Fact]
    public void ACompletedTask_AcceptsNoFurtherTransition()
    {
        var task = ChatBookingTask.Start(new ChatBookingTaskId(Guid.NewGuid()), TenantId, CalendarId, Now);
        task.ChooseService(ServiceId, Now);
        task.ChooseWorker(WorkerId, Now);
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
