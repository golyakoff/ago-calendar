using System.Globalization;
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
    /// <summary>`25-33`: how many raw slot rows to fetch from the read store before
    /// <c>ModuleStepFactory</c> groups them by day (the worker-choice step) or filters them to one
    /// already-chosen day (the date-choice step and every re-query of it). Reuses
    /// <see cref="GetOpenSlotsHandler.MaxLimit"/> itself rather than inventing a second ceiling: that
    /// constant's own reasoning ("bounded by the weakest renderer rather than the database") already
    /// covers this case, and this fetch needs to be generous *precisely because* it is not what gets
    /// rendered directly any more - <c>ModuleStepFactory.DateChoice</c>'s own page size is what
    /// protects the text channel now, not this number. Before `25-33` this same constant (then named
    /// <c>SlotPageSize</c>, fixed at ten) was both the query limit *and* the rendered page size in one
    /// number, because the old shape had no grouping step to separate the two - the flat-list-only
    /// reason it does not apply any more is exactly what the backlog item's own "revisit this
    /// constant's reasoning" instruction asked to be stated rather than assumed.</summary>
    private const int SlotQueryLimit = GetOpenSlotsHandler.MaxLimit;

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

        // `25-32`: a retried SubmitReplyAsync call - ModuleResiliencePipelines retries every exception,
        // including a timeout on a request Calendar already committed (that pipeline's own remarks
        // assumed a reply carries an idempotency key it does not) - resends the exact value that already
        // produced this task's *current* state. KindMatches, just below, cannot catch it: it checks the
        // wire shape only, and AwaitingServiceChoice/AwaitingWorkerChoice both expect a plain
        // choice_list, so the replayed value looks like a perfectly ordinary reply to the *next* step.
        // Left unguarded, it is silently misapplied there - a service id fed to GetOpenSlotsHandler as
        // if it were a worker id, producing a real date_time_picker step with genuinely zero slots,
        // while the worker-choice step this reply actually answered is never regenerated (this item's
        // own live evidence). ChatBookingTask.LastAppliedValue is the value that produced the state this
        // task is in right now; an incoming value identical to it is the same request being replayed,
        // not a fresh answer, so it is answered the same way the very first application already would
        // have: the step for the state that value produced, without touching anything a second time.
        if (task.LastAppliedValue == command.Value)
        {
            return await BuildStepForCurrentStateAsync(
                task, tenantPublicKey, command.Locale, command.KnownPhone, command.AcceptUnverifiedPhone,
                cancellationToken);
        }

        return task.State switch
        {
            ChatBookingTaskState.AwaitingServiceChoice =>
                await HandleServiceChosenAsync(
                    task, tenantPublicKey, command.Value, command.Locale, now, cancellationToken),
            ChatBookingTaskState.AwaitingWorkerChoice =>
                await HandleWorkerChosenAsync(
                    task, tenantPublicKey, command.Value, command.Locale, now, cancellationToken),
            ChatBookingTaskState.AwaitingDateChoice =>
                await HandleDateChosenAsync(
                    task, tenantPublicKey, command.Value, command.Locale, now, cancellationToken),
            ChatBookingTaskState.AwaitingSlotChoice =>
                await HandleSlotChosenAsync(
                    task, tenantPublicKey, command.Value, command.Locale, command.KnownPhone,
                    command.AcceptUnverifiedPhone, now, cancellationToken),
            ChatBookingTaskState.AwaitingPhone =>
                await HandlePhoneProvidedAsync(
                    task, tenantPublicKey, command.Value, command.PhoneVerifiedAt, command.AcceptUnverifiedPhone,
                    command.Locale, now, cancellationToken),
            _ => ChatModuleTaskErrors.AlreadyComplete(),
        };
    }

    private async Task<Result<ModuleTaskReplied>> HandleServiceChosenAsync(
        ChatBookingTask task, string tenantPublicKey, string value, string locale, DateTimeOffset now,
        CancellationToken cancellationToken)
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
            new ModuleTaskReplied(ModuleStepFactory.WorkerChoice(workers.Value, locale), Complete: false));
    }

    /// <summary>`25-33`: fetches broadly (<see cref="SlotQueryLimit"/>) rather than the old ten -
    /// this reply now feeds the date round's own day-grouping, and a narrow fetch could cut the date
    /// list short by never seeing a day past whichever slot happened to be the tenth row.</summary>
    private async Task<Result<ModuleTaskReplied>> HandleWorkerChosenAsync(
        ChatBookingTask task, string tenantPublicKey, string value, string locale, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(value, out var workerId))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        var slots = await slotsHandler.HandleAsync(
            new GetOpenSlots(
                tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
                workerId, SlotQueryLimit, Origin: null),
            cancellationToken);
        if (!slots.IsSuccess)
        {
            return slots.Error!.Value;
        }

        task.ChooseWorker(new WorkerId(workerId), now);
        await tasks.SaveAsync(task, cancellationToken);

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.DateChoice(slots.Value, locale), Complete: false));
    }

    /// <summary>`25-33`: the date round's own reply - parses the ISO date
    /// (<c>ModuleStepFactory.FormatDateValue</c>'s own remarks on the format), advances the task into
    /// the time round for that date, and sends the time round's own step. A date that fails to parse
    /// is <see cref="ChatModuleTaskErrors.InvalidReplyValue"/>, the identical treatment every other
    /// malformed id-shaped value in this handler already gets.</summary>
    private async Task<Result<ModuleTaskReplied>> HandleDateChosenAsync(
        ChatBookingTask task, string tenantPublicKey, string value, string locale, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return ChatModuleTaskErrors.InvalidReplyValue();
        }

        task.ChooseDate(date, now);
        await tasks.SaveAsync(task, cancellationToken);

        var slots = await GetSlotsForDateAsync(task, tenantPublicKey, date, cancellationToken);
        if (!slots.IsSuccess)
        {
            return slots.Error!.Value;
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.SlotChoice(slots.Value, date, locale), Complete: false));
    }

    /// <summary>`25-33`: the one place this handler re-queries a single date's own slots - the
    /// worker-choice reply's own broad fetch (<see cref="HandleWorkerChosenAsync"/>) is never cached
    /// anywhere, so every later step that needs "this date's slots" (the date round's own answer, a
    /// retried-reply rebuild, a lost-race re-offer) asks fresh, the same "ask the read side again"
    /// precedent this class's own <c>BuildStepForCurrentStateAsync</c> remarks already state for
    /// <see cref="RebuildWorkerChoiceAsync"/>. Fetches the identical broad <see cref="SlotQueryLimit"/>
    /// and filters client-side rather than adding a date parameter to <see cref="IBookingSurfaceReadStore.ListOpenSlotsAsync"/> -
    /// that port is shared with the public widget, which has no day-grouping concept of its own to
    /// justify widening its contract for this one caller.</summary>
    private async Task<Result<IReadOnlyList<OpenSlotRow>>> GetSlotsForDateAsync(
        ChatBookingTask task, string tenantPublicKey, DateOnly date, CancellationToken cancellationToken)
    {
        var slots = await slotsHandler.HandleAsync(
            new GetOpenSlots(
                tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value,
                task.WorkerId!.Value.Value, SlotQueryLimit, Origin: null),
            cancellationToken);
        if (!slots.IsSuccess)
        {
            return slots.Error!.Value;
        }

        return Result<IReadOnlyList<OpenSlotRow>>.Success([.. slots.Value.Where(s => s.LocalDate == date)]);
    }

    /// <summary>`25-32`: the response to a detected replay - see <c>HandleAsync</c>'s own remarks.
    /// Deliberately re-derives the step from the task's current fields rather than caching the original
    /// response anywhere: every value it needs (<see cref="Domain.ChatBookingTask.ServiceId"/>,
    /// <see cref="Domain.ChatBookingTask.WorkerId"/>) is already sitting on the aggregate this handler
    /// just loaded, and re-running the same read handler the first, successful application ran is the
    /// same "ask the read side again" precedent <c>HandlePhoneProvidedAsync</c>'s own lost-race path
    /// already sets, not a new pattern.</summary>
    private async Task<Result<ModuleTaskReplied>> BuildStepForCurrentStateAsync(
        ChatBookingTask task, string tenantPublicKey, string locale, string? knownPhone, bool acceptUnverifiedPhone,
        CancellationToken cancellationToken) =>
        task.State switch
        {
            ChatBookingTaskState.AwaitingWorkerChoice =>
                await RebuildWorkerChoiceAsync(task, tenantPublicKey, locale, cancellationToken),
            // `25-33`: AwaitingDateChoice can never get here, for the identical reason
            // AwaitingServiceChoice (below) cannot - LastAppliedValue while in this state is the
            // workerId that produced it, and a genuinely retried worker-choice reply always carries
            // kind choice_list, which KindMatches already refuses against this state's own
            // date_time_picker requirement before this switch is ever reached. AwaitingSlotChoice
            // just below is the one genuinely reachable same-kind pairing - see
            // ChatBookingTask.LastAppliedValue's own remarks on why a date-round replay and a
            // genuine time-round answer can never be confused for each other regardless.
            ChatBookingTaskState.AwaitingSlotChoice =>
                await RebuildSlotChoiceAsync(task, tenantPublicKey, locale, cancellationToken),
            // `25-39`: rebuilt from the *current* call's own AcceptUnverifiedPhone/KnownPhone, the
            // identical "never persisted, always resent" shape every other field on this request
            // already takes - see ReplyToModuleTask.Locale's own remarks. In practice this branch is
            // as unreachable as AwaitingServiceChoice's own (a phone-step reply's value is a typed
            // phone number, never the eventId LastAppliedValue holds), but it is not asserted
            // unreachable the way that one is, so it stays real rather than a placeholder.
            ChatBookingTaskState.AwaitingPhone =>
                Result<ModuleTaskReplied>.Success(new ModuleTaskReplied(
                    ModuleStepFactory.PhoneForm(locale, !acceptUnverifiedPhone, knownPhone), Complete: false)),
            // AwaitingServiceChoice can never get here - LastAppliedValue is still null the only time
            // the task is in that state, so it can never equal a real command.Value. Completed is
            // intercepted above, before KindMatches even runs. Refused rather than silently doing
            // nothing if either invariant is ever wrong.
            _ => ChatModuleTaskErrors.KindMismatch(),
        };

    private async Task<Result<ModuleTaskReplied>> RebuildWorkerChoiceAsync(
        ChatBookingTask task, string tenantPublicKey, string locale, CancellationToken cancellationToken)
    {
        var workers = await workersHandler.HandleAsync(
            new GetBookableWorkers(tenantPublicKey, task.CalendarId.Value, task.ServiceId!.Value.Value, Origin: null),
            cancellationToken);
        if (!workers.IsSuccess)
        {
            return workers.Error!.Value;
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.WorkerChoice(workers.Value, locale), Complete: false));
    }

    /// <summary>`25-33`: rebuilds the time round for <see cref="ChatBookingTask.SelectedDate"/> - the
    /// date the *original* application of this replayed value (a date-choice reply) already chose.
    /// <c>SelectedDate</c> is never null here: the only way to reach <see cref="ChatBookingTaskState.AwaitingSlotChoice"/>
    /// at all is through <see cref="ChatBookingTask.ChooseDate"/>, which sets it in the same
    /// transition.</summary>
    private async Task<Result<ModuleTaskReplied>> RebuildSlotChoiceAsync(
        ChatBookingTask task, string tenantPublicKey, string locale, CancellationToken cancellationToken)
    {
        var date = task.SelectedDate!.Value;
        var slots = await GetSlotsForDateAsync(task, tenantPublicKey, date, cancellationToken);
        if (!slots.IsSuccess)
        {
            return slots.Error!.Value;
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.SlotChoice(slots.Value, date, locale), Complete: false));
    }

    /// <summary>
    /// `25-39`: the setting's own sharpened core. `ChooseSlot` already moves the task to
    /// <see cref="ChatBookingTaskState.AwaitingPhone"/> below regardless of what happens next - that
    /// transition is real either way, because <see cref="HandlePhoneProvidedAsync"/>'s own
    /// <c>task.Complete</c>/<c>task.ReopenForSlotChoice</c> both require it. What differs is only
    /// whether this method returns a <c>phone</c>-shaped <see cref="ModuleStep"/> for the visitor to
    /// answer, or calls straight through to what that visitor's own answer would have triggered
    /// anyway.
    ///
    /// <para><b>Two cases, named explicitly rather than left as one merged condition.</b> With
    /// <paramref name="acceptUnverifiedPhone"/> on and <paramref name="knownPhone"/> already known
    /// (`25-39`'s own "common path"), this calls <see cref="HandlePhoneProvidedAsync"/> directly with
    /// that number and <c>phoneVerifiedAt: null</c> - no phone-form step is ever built, let alone
    /// returned; the visitor who just picked a slot sees the booking complete. With the setting on but
    /// nothing known yet (the item's own explicitly-named "fallback, not the common path"), the phone
    /// step still renders, but as a plain <see cref="ModuleStepKind.Form"/> rather than
    /// <see cref="ModuleStepKind.VerifiedPhoneForm"/> - see <see cref="ModuleStepFactory.PhoneForm"/>'s
    /// own remarks for why that distinction is what actually turns Chat's own verification gate off,
    /// not merely a hint. With the setting off (today's default for every tenant that has not asked for
    /// it), neither case ever triggers: <c>!acceptUnverifiedPhone</c> is <see langword="true"/> and this
    /// method's own condition below is false by construction, so the plain, unlocalized-behaviour-
    /// change-free path this method always took stays exactly as it was.</para>
    /// </summary>
    private async Task<Result<ModuleTaskReplied>> HandleSlotChosenAsync(
        ChatBookingTask task, string tenantPublicKey, string value, string locale, string? knownPhone,
        bool acceptUnverifiedPhone, DateTimeOffset now, CancellationToken cancellationToken)
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

        if (acceptUnverifiedPhone && knownPhone is { } phone)
        {
            return await HandlePhoneProvidedAsync(
                task, tenantPublicKey, phone, phoneVerifiedAt: null, acceptUnverifiedPhone, locale, now,
                cancellationToken);
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.PhoneForm(locale, !acceptUnverifiedPhone, knownPhone), Complete: false));
    }

    private async Task<Result<ModuleTaskReplied>> HandlePhoneProvidedAsync(
        ChatBookingTask task, string tenantPublicKey, string phone, DateTimeOffset? phoneVerifiedAt,
        bool acceptUnverifiedPhone, string locale, DateTimeOffset now, CancellationToken cancellationToken)
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
        //
        // `25-39`: RequiresVerifiedPhone is `!acceptUnverifiedPhone`, not a hardcoded `true` - the one
        // line that actually relaxes `20-09`'s gate for a tenant who asked for it, everywhere this
        // method is reached from (a genuine phone-form reply, or HandleSlotChosenAsync's own direct
        // call for the skip-the-step path). Never a silent "treat as verified": `phoneVerifiedAt`
        // stays exactly what the caller passed - null on the skip path - so a booking taken this way
        // is recorded with no verification instant at all, the same honest signal every other
        // never-verified customer row already carries (BookingAttempt.PhoneVerifiedAt's own remarks on
        // why this is never overwritten once set, in either direction).
        var outcome = await bookHandler.HandleAsync(
            new UseCases.BookEvent.BookEvent(
                task.CalendarId, task.EventId!.Value, task.ServiceId!.Value, phone,
                DisplayName: null, Origin: null, RequiresVerifiedPhone: !acceptUnverifiedPhone,
                PhoneVerifiedAt: phoneVerifiedAt),
            cancellationToken);

        if (outcome.Booking is { } booking)
        {
            task.Complete(phone, now);
            await tasks.SaveAsync(task, cancellationToken);

            var (serviceName, workerName) = await DescribeBookingAsync(task, booking, cancellationToken);
            var step = ModuleStepFactory.Confirmation(
                serviceName, workerName, booking.Slot.StartsAt, booking.Slot.EndsAt, locale);
            return Result<ModuleTaskReplied>.Success(new ModuleTaskReplied(step, Complete: true));
        }

        // Lost the race, or the phone was rejected, or the caller was rate-limited - every one of
        // these is BookEventHandler's own ordinary rejection (never an exception), and the backlog
        // item's own words are that the visitor must never see a dead end for it. Rather than surface
        // outcome.Error as a hard failure, re-offer fresh slots for the same worker.
        //
        // `25-33`: re-offered for the *same date* the visitor already picked, not the date round
        // again - ReopenForSlotChoice's own remarks: only the slot that just lost the race needs
        // re-picking, not the day or the worker.
        task.ReopenForSlotChoice(phone, now);
        await tasks.SaveAsync(task, cancellationToken);

        var date = task.SelectedDate!.Value;
        var slots = await GetSlotsForDateAsync(task, tenantPublicKey, date, cancellationToken);
        if (!slots.IsSuccess)
        {
            // The configured calendar itself stopped resolving mid-task (unpublished under us,
            // most plausibly) - genuinely nothing left to re-offer.
            return slots.Error!.Value;
        }

        return Result<ModuleTaskReplied>.Success(
            new ModuleTaskReplied(ModuleStepFactory.SlotChoice(slots.Value, date, locale), Complete: false));
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
        // `25-33`: the date round and the time round below share one wire kind - both are
        // date_time_picker, the vocabulary's own answer to "two rounds, no fifth kind"
        // (ChatBookingTaskState.AwaitingDateChoice's own remarks). A reply meant for one can never be
        // mistaken for the other by KindMatches alone (both pass this check identically); what tells
        // them apart is State itself, exactly as it already tells apart AwaitingWorkerChoice's own
        // choice_list from AwaitingServiceChoice's.
        ChatBookingTaskState.AwaitingDateChoice => kind == ModuleStepKinds.DateTimePicker,
        ChatBookingTaskState.AwaitingSlotChoice => kind == ModuleStepKinds.DateTimePicker,
        // `20-09`: PhoneForm() emits VerifiedPhoneForm by default - see ModuleStepFactory's own
        // remarks. `25-39`: also accepts plain Form - the kind PhoneForm emits instead when a
        // tenant's own AcceptUnverifiedPhone setting is on and no phone was already known
        // (ModuleStepFactory.PhoneForm's own remarks). Accepting both here does not weaken `20-09`'s
        // guarantee: the real gate is HandlePhoneProvidedAsync's own `RequiresVerifiedPhone:
        // !acceptUnverifiedPhone`, computed fresh from this same reply's own flag, and Chat's own
        // separate gate (RouteConversationToModuleHandler.ContinueActiveTaskAsync) decides whether to
        // demand `14-15` evidence from its own persisted LastStepKind - the actual kind this factory
        // sent when the step was rendered, not a live setting a caller could otherwise race. This
        // check only decides whether a reply's wire shape matches what this task is waiting on, which
        // is legitimately either kind depending on the setting at render time.
        ChatBookingTaskState.AwaitingPhone =>
            kind == ModuleStepKinds.VerifiedPhoneForm || kind == ModuleStepKinds.Form,
        _ => false,
    };
}
