using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// `26-322`: the one place the worker step is composed, shared by <see cref="StartModuleTaskHandler"/>
/// (which reaches it after auto-choosing a sole service) and <see cref="ReplyToModuleTaskHandler"/>
/// (which reaches it after the visitor picks a service). Both must offer the worker choice under the
/// identical rule - unless exactly one worker is eligible for the chosen service, in which case skip
/// straight to the date round - so it lives here once rather than in two copies that could silently
/// drift apart. Application-layer only: it composes two existing Application use cases
/// (<see cref="GetBookableWorkersHandler"/>, <see cref="GetOpenSlotsHandler"/>) and touches only the
/// domain aggregate, so it introduces no new dependency direction. A static helper rather than a third
/// injected handler because it holds no state and wires nothing - the alternative, a DI-registered
/// service, would add a lifetime and a registration for what is one pure branch over two calls.
///
/// <para>It mutates the passed <paramref name="task"/> (an auto-skip records the sole worker through
/// <see cref="ChatBookingTask.AutoChooseWorker"/>) but never persists it: saving is the calling
/// handler's own transaction boundary, the same split every handler in this folder already keeps
/// between deciding and committing.</para>
/// </summary>
internal static class ChatBookingStepComposer
{
    /// <summary>
    /// Given a <paramref name="task"/> that has just entered
    /// <see cref="ChatBookingTaskState.AwaitingWorkerChoice"/> (its service already chosen), produces the
    /// step to send next: the worker choice, or - when exactly one worker is eligible - an auto-skip to
    /// the date round for that worker.
    /// </summary>
    public static async Task<Result<ModuleStep>> WorkerStepAsync(
        ChatBookingTask task,
        string tenantPublicKey,
        string locale,
        DateTimeOffset now,
        GetBookableWorkersHandler workersHandler,
        GetOpenSlotsHandler slotsHandler,
        CancellationToken cancellationToken)
    {
        var workers = await workersHandler.HandleAsync(
            new GetBookableWorkers(tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value, Origin: null),
            cancellationToken);
        if (!workers.IsSuccess)
        {
            return workers.Error!.Value;
        }

        // `26-322`: exactly one eligible worker is not a choice - auto-select it and jump to the date
        // round, the same "a list of one is not a question" reasoning the sole-service skip applies one
        // step earlier. Zero workers is deliberately NOT a skip: it stays the empty worker-choice step
        // this flow already produced (GetBookingSurfaceHandler's own "empty is a real state" precedent),
        // its own honest "nothing bookable here yet" signal rather than a silent dead end.
        if (workers.Value.Count == 1)
        {
            var sole = workers.Value[0];

            // `25-33`: fetch broadly (GetOpenSlotsHandler.MaxLimit) rather than a narrow page - the date
            // round groups these by day, and a narrow fetch could cut the date list short by never
            // seeing a day past whichever slot happened to be the last row. The identical limit
            // HandleWorkerChosenAsync uses when the visitor picks a worker by hand.
            var slots = await slotsHandler.HandleAsync(
                new GetOpenSlots(
                    tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value, sole.WorkerId.Value,
                    GetOpenSlotsHandler.MaxLimit, Origin: null),
                cancellationToken);
            if (!slots.IsSuccess)
            {
                return slots.Error!.Value;
            }

            task.AutoChooseWorker(sole.WorkerId, now);
            return Result<ModuleStep>.Success(ModuleStepFactory.DateChoice(slots.Value, locale));
        }

        return Result<ModuleStep>.Success(ModuleStepFactory.WorkerChoice(workers.Value, locale));
    }
}
