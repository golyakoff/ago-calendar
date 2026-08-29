using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// Advances one <see cref="ChatBookingTask"/> by exactly one step, driven entirely by which
/// <see cref="ChatBookingTaskState"/> it is currently waiting on - the reply's own <c>value</c> is
/// interpreted differently at every step, and the aggregate's <c>RequireState</c> guard is the second
/// line of defence against a reply arriving for the wrong one (the first is the kind check below,
/// which produces a caller-legible error instead of an <see cref="InvalidChatBookingTaskStateException"/>).
///
/// <para><b>Never validates that <c>value</c> was actually one of the options a step offered, beyond
/// "is this a well-formed id".</b> That is deliberate rather than a gap: every id this handler forwards
/// still passes through the same existing handler every other caller of it does
/// (<see cref="GetBookableWorkersHandler"/>, <see cref="GetOpenSlotsHandler"/>,
/// <see cref="BookEventHandler"/>), and those already turn "that id does not exist" or "that id is no
/// longer valid" into their own ordinary empty result or rejection. Re-checking it here would be a
/// second copy of validation those handlers already own, and nothing here can be pointed at another
/// tenant's data by a bad id regardless - <see cref="ChatBookingTask.CalendarId"/> comes only from
/// this deployment's static configuration, never from the reply.</para>
/// </summary>
public sealed class ReplyToModuleTaskHandler(
    IChatBookingTaskStore tasks,
    GetBookableWorkersHandler workersHandler,
    GetOpenSlotsHandler slotsHandler,
    IBookingSurfaceReadStore surface,
    BookEventHandler bookHandler,
    ChatModuleTaskOptions options,
    IClock clock)
{
    /// <summary>How many slots one <c>date_time_picker</c> step offers. Ten, not
    /// <c>GetOpenSlotsHandler.MaxLimit</c> and not the widget's own <c>DefaultSlotLimit</c> of sixty:
    /// this is a different renderer with a different ceiling, and the whole point of the closed
    /// primitive vocabulary is that each one picks what it can survive - a text channel printing a
    /// numbered list of sixty times is not a menu, it is a wall of text.</summary>
    private const int SlotPageSize = 10;

    public async Task<Result<ModuleTaskReplied>> HandleAsync(
        ReplyToModuleTask command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.ExternalTaskId, out var taskGuid))
        {
            return ChatModuleTaskErrors.TaskNotFound();
        }

        var task = await tasks.GetByIdAsync(new ChatBookingTaskId(taskGuid), cancellationToken);
        if (task is null)
        {
            return ChatModuleTaskErrors.TaskNotFound();
        }

        if (task.State == ChatBookingTaskState.Completed)
        {
            return ChatModuleTaskErrors.AlreadyComplete();
        }

        if (!KindMatches(task.State, command.Kind))
        {
            return ChatModuleTaskErrors.KindMismatch();
        }

        var now = clock.UtcNow;

        return task.State switch
        {
            ChatBookingTaskState.AwaitingServiceChoice =>
                await HandleServiceChosenAsync(task, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingWorkerChoice =>
                await HandleWorkerChosenAsync(task, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingSlotChoice =>
                await HandleSlotChosenAsync(task, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingPhone =>
                await HandlePhoneProvidedAsync(task, command.Value, now, cancellationToken),
            _ => ChatModuleTaskErrors.AlreadyComplete(),
        };
    }

    private async Task<Result<ModuleTaskReplied>> HandleServiceChosenAsync(
        ChatBookingTask task, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var serviceId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        var workers = await workersHandler.HandleAsync(
            new GetBookableWorkers(options.TenantPublicKey, task.CalendarId.Value, serviceId, Origin: null),
            cancellationToken);
        if (!workers.IsSuccess)
        {
            return workers.Error!.Value;
        }

        task.ChooseService(new ServiceId(serviceId), now);
        await tasks.SaveAsync(task, cancellationToken);

        // Empty is a real state, not special-cased - see ModuleStepFactory and the item's own report
        // for why this deliberately mirrors GetBookingSurfaceHandler's own precedent.
        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.WorkerChoice(workers.Value), Complete: false));
    }

    private async Task<Result<ModuleTaskReplied>> HandleWorkerChosenAsync(
        ChatBookingTask task, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var workerId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        var slots = await slotsHandler.HandleAsync(
            new GetOpenSlots(
                options.TenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
                workerId, SlotPageSize, Origin: null),
            cancellationToken);
        if (!slots.IsSuccess)
        {
            return slots.Error!.Value;
        }

        task.ChooseWorker(new WorkerId(workerId), now);
        await tasks.SaveAsync(task, cancellationToken);

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.SlotChoice(slots.Value), Complete: false));
    }

    private async Task<Result<ModuleTaskReplied>> HandleSlotChosenAsync(
        ChatBookingTask task, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var eventId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        // No availability check here - it would be a stale one anyway, exactly the reasoning
        // GetOpenSlotsHandler's own remarks give for why its own read is a courtesy. BookEventHandler
        // makes the real, atomic decision once the phone number arrives.
        task.ChooseSlot(new EventId(eventId), now);
        await tasks.SaveAsync(task, cancellationToken);

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.PhoneForm(), Complete: false));
    }

    private async Task<Result<ModuleTaskReplied>> HandlePhoneProvidedAsync(
        ChatBookingTask task, string phone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Qualified, not a bare `new BookEvent(...)`: this file's `using` for the BookEvent use-case
        // folder brings in a namespace named BookEvent alongside the command record of the same
        // name, and the compiler resolves the bare identifier to the namespace (CS0118) - the same
        // family of collision BookingEndpoints already documents for the domain's own EventId versus
        // Microsoft.Extensions.Logging's.
        var outcome = await bookHandler.HandleAsync(
            new UseCases.BookEvent.BookEvent(
                task.CalendarId, task.EventId!.Value, task.ServiceId!.Value, phone,
                DisplayName: null, Origin: null),
            cancellationToken);

        if (outcome.Booking is { } booking)
        {
            task.Complete(phone, now);
            await tasks.SaveAsync(task, cancellationToken);

            var (serviceName, workerName) = await DescribeBookingAsync(task, booking, cancellationToken);
            var step = ModuleStepFactory.Confirmation(
                serviceName, workerName, booking.Slot.StartsAt, booking.Slot.EndsAt);
            return Result<ModuleTaskReplied>.Success(new ModuleTaskReplied(step, Complete: true));
        }

        // Lost the race, or the phone was rejected, or the caller was rate-limited - every one of
        // these is BookEventHandler's own ordinary rejection (never an exception), and the backlog
        // item's own words are that the visitor must never see a dead end for it. Rather than surface
        // outcome.Error as a hard failure, re-offer fresh slots for the same worker.
        task.ReopenForSlotChoice(phone, now);
        await tasks.SaveAsync(task, cancellationToken);

        var slots = await slotsHandler.HandleAsync(
            new GetOpenSlots(
                options.TenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
                task.WorkerId!.Value.Value, SlotPageSize, Origin: null),
            cancellationToken);
        if (!slots.IsSuccess)
        {
            // The configured calendar itself stopped resolving mid-task (unpublished under us,
            // most plausibly) - genuinely nothing left to re-offer.
            return slots.Error!.Value;
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.SlotChoice(slots.Value), Complete: false));
    }

    /// <summary>The names a confirmation card needs, which <see cref="BookingConfirmation"/> itself
    /// does not carry - it is read back from the claim's own <c>RETURNING</c>, and a claim writes ids,
    /// not display strings (see <c>IBookingStore</c>'s own remarks). Falls back to a generic word
    /// rather than throwing if a name has since changed or a row cannot be found, because a
    /// confirmation card for a booking that already succeeded must never fail to render over a
    /// missing label.</summary>
    private async Task<(string ServiceName, string WorkerName)> DescribeBookingAsync(
        ChatBookingTask task, BookingConfirmation booking, CancellationToken cancellationToken)
    {
        var services = await surface.ListServicesAsync(task.CalendarId, cancellationToken);
        var serviceName = services
            .FirstOrDefault(s => s.ServiceId == task.ServiceId!.Value)
            .Name ?? "your service";

        var workers = await surface.ListWorkersAsync(task.CalendarId, task.ServiceId!.Value, cancellationToken);
        var workerName = workers
            .FirstOrDefault(w => w.WorkerId == booking.WorkerId)
            .DisplayName ?? "the team";

        return (serviceName, workerName);
    }

    private static bool KindMatches(ChatBookingTaskState state, string kind) => state switch
    {
        ChatBookingTaskState.AwaitingServiceChoice => kind == ModuleStepKinds.ChoiceList,
        ChatBookingTaskState.AwaitingWorkerChoice => kind == ModuleStepKinds.ChoiceList,
        ChatBookingTaskState.AwaitingSlotChoice => kind == ModuleStepKinds.DateTimePicker,
        ChatBookingTaskState.AwaitingPhone => kind == ModuleStepKinds.Form,
        _ => false,
    };
}
