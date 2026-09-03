using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// This surface's own expected failures, in the same <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary
/// the rest of this product uses. Unlike <c>PublicBookingErrors</c>, this endpoint is not
/// unauthenticated-from-a-stranger in the same sense - its only caller is meant to be `Ago.Chat.*`'s
/// own server, over a link this item's own report names as carrying no service-to-service auth yet -
/// so there is no enumeration concern driving these messages to be deliberately vague the way the
/// public booking surface's are.
/// </summary>
public static class ChatModuleTaskErrors
{
    /// <summary>`22-04`: this call's own claimed site has no provisioned tenant, or that tenant does
    /// not have exactly one published calendar to answer chat with - see
    /// <c>StartModuleTaskHandler</c>'s own remarks on why "more than one" refuses rather than guesses.
    /// A per-call, caller-visible state now (this site is not set up for the calendar module), not the
    /// single deployment-wide fault it used to be before per-site resolution replaced the pinned
    /// tenant - see <c>ErrorExtensions</c> for the status code this maps to today.</summary>
    public static Error NotConfigured() => new(
        "chat_module_task.not_configured",
        "This site's calendar module is not configured with exactly one published calendar.");

    public static Error TaskNotFound() => new(
        "chat_module_task.not_found",
        "No module task answers to that id.");

    public static Error AlreadyComplete() => new(
        "chat_module_task.already_complete",
        "This task already finished; start a new one to book again.");

    /// <summary>The reply's own <c>kind</c> does not match the step this task is actually waiting on -
    /// most likely a caller that lost track of which step it last received.</summary>
    public static Error KindMismatch() => new(
        "chat_module_task.kind_mismatch",
        "That reply's kind does not match the step this task is currently waiting on.");

    /// <summary><c>value</c> is not a well-formed id for a step that expects one (every kind except
    /// <c>form</c>). Never how "that id does not exist" or "that id is not currently offered" is
    /// reported - those reach the same downstream handler every other caller of it does, and come
    /// back as that handler's own ordinary empty-result or rejection, not as this error.</summary>
    public static Error InvalidReplyValue() => new(
        "chat_module_task.invalid_reply_value",
        "That reply's value is not a valid selection for this step.");
}
