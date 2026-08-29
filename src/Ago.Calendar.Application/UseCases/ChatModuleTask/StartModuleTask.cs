namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// A visitor entered this deployment's calendar module from a chat conversation.
/// </summary>
/// <param name="ChatTaskId">Chat's own id for this task. Opaque - accepted and never stored; see
/// <see cref="Domain.ChatBookingTask"/>'s own remarks on why holding it would be the mirror image of
/// the boundary `adr/0027` already protects.</param>
/// <param name="SiteId">Opaque, same reason.</param>
/// <param name="ConversationId">Opaque, same reason.</param>
/// <param name="TriggerText">What the visitor typed to enter the module (e.g. <c>/booking</c>).
/// Unused today - `adr/0065` decided v1 has no intent detection, so the entry point is a fixed menu
/// regardless of what was typed - but the wire contract carries it, so the command does too, rather
/// than silently dropping a field a future item might read.</param>
public readonly record struct StartModuleTask(
    Guid ChatTaskId, Guid SiteId, Guid ConversationId, string TriggerText);

public readonly record struct ModuleTaskStarted(string ExternalTaskId, ModuleStep Step, bool Complete);
