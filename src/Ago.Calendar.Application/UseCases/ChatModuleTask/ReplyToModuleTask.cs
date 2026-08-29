namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <param name="ExternalTaskId">From the route - this task's own id in string form.</param>
/// <param name="ChatTaskId">Opaque, accepted and never stored - see <see cref="StartModuleTask"/>.</param>
/// <param name="Kind">Echoes the step's own <c>kind</c>. Checked against what this task is actually
/// waiting on (<see cref="Domain.ChatBookingTaskState"/>) before the value is interpreted.</param>
/// <param name="Value">For a choice-shaped step, one of that step's own action values. For a
/// <c>form</c> step, the visitor's raw typed text, unvalidated by Chat.</param>
public readonly record struct ReplyToModuleTask(string ExternalTaskId, Guid ChatTaskId, string Kind, string Value);

public readonly record struct ModuleTaskReplied(ModuleStep? Step, bool Complete);
