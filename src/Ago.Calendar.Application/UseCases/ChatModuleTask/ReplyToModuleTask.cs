namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <param name="ExternalTaskId">From the route - this task's own id in string form.</param>
/// <param name="ChatTaskId">Opaque, accepted and never stored - see <see cref="StartModuleTask"/>.</param>
/// <param name="Kind">Echoes the step's own <c>kind</c>. Checked against what this task is actually
/// waiting on (<see cref="Domain.ChatBookingTaskState"/>) before the value is interpreted.</param>
/// <param name="Value">For a choice-shaped step, one of that step's own action values. For a
/// <c>form</c> step, the visitor's raw typed text, unvalidated by Chat.</param>
/// <param name="PhoneVerifiedAt">`20-09`: Chat's own assertion, present only on a reply answering a
/// <c>verified_phone_form</c> step for which it found a completed `14-15` verification - see
/// <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.PhoneVerifiedAt</c>'s own remarks. Threaded
/// unchanged into <c>BookEvent.PhoneVerifiedAt</c> by <see cref="ReplyToModuleTaskHandler"/>.</param>
/// <param name="CredentialSiteId">`22-04`: the site id <c>Ago.Calendar.Api.ChatModule.ChatModuleTaskEndpoints</c>'s
/// own credential check proved this call is for - always present once authenticated, now that
/// <see cref="Domain.ChatBookingTask.TenantId"/> gives this route the per-task identity adr/0094 named
/// as missing here (unlike its own Start route, which cross-checks against the request body instead).
/// Checked against that task's own <c>TenantId</c> by <see cref="ReplyToModuleTaskHandler"/> - a
/// credential proven for tenant A is refused outright against a task belonging to tenant B, the
/// identical property <c>Ago.Faq.Application.UseCases.FaqModuleTask.ReplyToFaqModuleTask.CredentialSiteId</c>'s
/// own remarks already prove for that module.</param>
/// <param name="Locale">`25-37`: resent on every reply, not merely at task start
/// (<see cref="StartModuleTask.Locale"/>'s own remarks) - this aggregate never stores it, so every
/// step after the first re-reads it straight off the request that is answering the previous one. See
/// <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.Locale</c>'s own remarks for why resending beats
/// persisting here.</param>
/// <param name="KnownPhone">`25-38`/`25-39`: the most recent phone number the visitor gave earlier in
/// the conversation, self-reported and never proven reachable - <see langword="null"/> when nothing
/// was ever recorded. See <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.KnownPhone</c>'s own
/// remarks for what <see cref="ReplyToModuleTaskHandler"/> does with it.</param>
/// <param name="AcceptUnverifiedPhone">`25-39`: this site's own temporary, off-by-default relaxation
/// of the verified-phone requirement - see
/// <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.AcceptUnverifiedPhone</c>'s own remarks.</param>
/// <param name="PersonId">`26-136`/`adr/0184`: chat's own visitor (person) id, threaded onto the booked
/// <c>Event</c> when this reply completes a booking. Null on a reply from a chat client predating this
/// item; <c>BookEventHandler</c> mints one locally in that case. See
/// <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.PersonId</c>'s own remarks.</param>
/// <param name="OriginConversationId">`26-136`/`adr/0184`: chat's own originating conversation id,
/// threaded opaquely onto the booked <c>Event</c> - null when absent. See
/// <c>Ago.Calendar.Contracts.ModuleTaskReplyRequest.OriginConversationId</c>'s own remarks.</param>
public readonly record struct ReplyToModuleTask(
    string ExternalTaskId, Guid ChatTaskId, string Kind, string Value, DateTimeOffset? PhoneVerifiedAt = null,
    Guid? CredentialSiteId = null, string Locale = "En", string? KnownPhone = null,
    bool AcceptUnverifiedPhone = false, Guid? PersonId = null, Guid? OriginConversationId = null);

public readonly record struct ModuleTaskReplied(ModuleStep? Step, bool Complete);
