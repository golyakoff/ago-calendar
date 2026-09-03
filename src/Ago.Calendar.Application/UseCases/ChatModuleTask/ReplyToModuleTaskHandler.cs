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
/// <para><b>`22-04`: the credential's own site id is cross-checked against the task's own
/// <see cref="ChatBookingTask.TenantId"/></b> before any step logic runs - the asymmetry adr/0094
/// named between this route and Calendar's Start route (which cross-checks the request body instead)
/// closes here: <see cref="ChatBookingTask"/> now carries a real tenant id, so a credential proven for
/// tenant A is refused outright against a task belonging to tenant B, reported identically to
/// <see cref="ChatModuleTaskErrors.TaskNotFound"/> - the same "do not confirm a resource's existence to
/// a caller not entitled to it" reasoning <c>PublicBookingErrors</c> already applies, and the identical
/// choice <c>Ago.Faq.Application.UseCases.FaqModuleTask.ReplyToFaqModuleTaskHandler</c>'s own remarks
/// make for the sibling product.</para>
///
/// <para><b>`22-04`: the tenant's <see cref="TenantPublicKey"/> is resolved fresh from
/// <see cref="ChatBookingTask.TenantId"/></b>, not read from a static deployment setting - every
/// downstream call this handler makes still goes through the public-key-shaped
/// <see cref="GetBookableWorkersHandler"/>/<see cref="GetOpenSlotsHandler"/> unchanged, because those
/// handlers are shared with the public widget and rewriting them to also accept a raw id would be a
/// second resolution path for the exact value <see cref="EmbedScopeResolver"/> already turns a key
/// into. One extra indexed lookup per reply, the same "second primary-key read" cost
/// <c>StartModuleTaskHandler</c>'s own remarks already accept for the identical reason.</para>
///
/// <para><b>Never validates that <c>value</c> was actually one of the options a step offered, beyond
/// "is this a well-formed id".</b> That is deliberate rather than a gap: every id this handler forwards
/// still passes through the same existing handler every other caller of it does
/// (<see cref="GetBookableWorkersHandler"/>, <see cref="GetOpenSlotsHandler"/>,
/// <see cref="BookEventHandler"/>), and those already turn "that id does not exist" or "that id is no
/// longer valid" into their own ordinary empty result or rejection. Re-checking it here would be a
/// second copy of validation those handlers already own.</para>
/// </summary>
public sealed class ReplyToModuleTaskHandler(
    IChatBookingTaskStore tasks,
    ITenantRepository tenants,
    GetBookableWorkersHandler workersHandler,
    GetOpenSlotsHandler slotsHandler,
    IBookingSurfaceReadStore surface,
    BookEventHandler bookHandler,
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

        // `22-04`: see this class's own remarks - a credential proven for another tenant is refused
        // as if this task did not exist, before any step logic (including AlreadyComplete) runs.
        if (command.CredentialSiteId is { } credentialSiteId && credentialSiteId != task.TenantId.Value)
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

        var tenant = await tenants.GetByIdAsync(task.TenantId, cancellationToken);
        if (tenant is null)
        {
            // The tenant existed when this task was started and is gone now - genuinely nothing left
            // to reply against.
            return ChatModuleTaskErrors.NotConfigured();
        }

        var tenantPublicKey = tenant.PublicKey.Value;
        var now = clock.UtcNow;

        return task.State switch
        {
            ChatBookingTaskState.AwaitingServiceChoice =>
                await HandleServiceChosenAsync(task, tenantPublicKey, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingWorkerChoice =>
                await HandleWorkerChosenAsync(task, tenantPublicKey, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingSlotChoice =>
                await HandleSlotChosenAsync(task, command.Value, now, cancellationToken),
            ChatBookingTaskState.AwaitingPhone =>
                await HandlePhoneProvidedAsync(
                    task, tenantPublicKey, command.Value, command.PhoneVerifiedAt, now, cancellationToken),
            _ => ChatModuleTaskErrors.AlreadyComplete(),
        };
    }

    private async Task<Result<ModuleTaskReplied>> HandleServiceChosenAsync(
        ChatBookingTask task, string tenantPublicKey, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var serviceId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        var workers = await workersHandler.HandleAsync(
            new GetBookableWorkers(tenantPublicKey, task.CalendarId.Value, serviceId, Origin: null),
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
        ChatBookingTask task, string tenantPublicKey, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var workerId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        var slots = await slotsHandler.HandleAsync(
            new GetOpenSlots(
                tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
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
        ChatBookingTask task, string tenantPublicKey, string phone, DateTimeOffset? phoneVerifiedAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Qualified, not a bare `new BookEvent(...)`: this file's `using` for the BookEvent use-case
        // folder brings in a namespace named BookEvent alongside the command record of the same
        // name, and the compiler resolves the bare identifier to the namespace (CS0118) - the same
        // family of collision BookingEndpoints already documents for the domain's own EventId versus
        // Microsoft.Extensions.Logging's.
        //
        // `20-09`: phoneVerifiedAt is threaded through unchanged - Chat's own assertion, checked
        // against its own `14-15` evidence before this reply was ever sent (RouteConversationToModuleHandler's
        // own remarks). This handler does not re-check it; BookEventHandler is where a missing
        // assertion is refused, the same "the module never re-validates what the caller already
        // validated" split this file's own remarks draw for a reply's id.
        var outcome = await bookHandler.HandleAsync(
            new UseCases.BookEvent.BookEvent(
                task.CalendarId, task.EventId!.Value, task.ServiceId!.Value, phone,
                DisplayName: null, Origin: null, RequiresVerifiedPhone: true, PhoneVerifiedAt: phoneVerifiedAt),
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
                tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
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
        // `20-09`: PhoneForm() now emits VerifiedPhoneForm, not plain Form - see ModuleStepFactory's
        // own remarks.
        ChatBookingTaskState.AwaitingPhone => kind == ModuleStepKinds.VerifiedPhoneForm,
        _ => false,
    };
}
