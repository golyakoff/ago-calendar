using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// Turns the existing `PublicBooking` read rows into <see cref="ModuleStep"/>s. Shared by
/// <c>StartModuleTaskHandler</c> and <c>ReplyToModuleTaskHandler</c> so the mapping from, say, a
/// <see cref="BookableWorkerRow"/> to a choice action is written once - the same reason
/// <c>EmbedScopeResolver</c> is one class rather than three copies of a preamble.
/// </summary>
internal static class ModuleStepFactory
{
    public static ModuleStep ServiceChoice(IReadOnlyList<BookableServiceRow> services) =>
        ModuleStep.ChoiceListStep(
            "What would you like to book?",
            [.. services.Select(s => new ModuleAction(DescribeService(s), s.ServiceId.Value.ToString()))]);

    public static ModuleStep WorkerChoice(IReadOnlyList<BookableWorkerRow> workers) =>
        ModuleStep.ChoiceListStep(
            "Who would you like to book with?",
            [.. workers.Select(w => new ModuleAction(w.DisplayName, w.WorkerId.Value.ToString()))]);

    public static ModuleStep SlotChoice(IReadOnlyList<OpenSlotRow> slots) =>
        ModuleStep.DateTimePickerStep(
            "Pick a time:",
            [.. slots.Select(s => new SlotOption(s.EventId.Value.ToString(), s.StartsAt, DescribeSlot(s)))],
            [.. slots.Select(s => new ModuleAction(DescribeSlot(s), s.EventId.Value.ToString()))]);

    public static ModuleStep PhoneForm() =>
        ModuleStep.FormStep("What's the best phone number to reach you on?", "phone", "Phone number");

    public static ModuleStep Confirmation(
        string serviceName, string workerName, DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        ModuleStep.ConfirmationStep(
            "You're booked!",
            [
                new ConfirmationLine("Service", serviceName),
                new ConfirmationLine("With", workerName),
                new ConfirmationLine("When", DescribeRange(startsAt, endsAt)),
            ]);

    private static string DescribeService(BookableServiceRow service) =>
        $"{service.Name} ({service.DurationMinutes} min)";

    private static string DescribeSlot(OpenSlotRow slot) => DescribeRange(slot.StartsAt, slot.EndsAt);

    /// <summary>UTC, labelled - date-and-time.md rule 1: no IANA zone is known for the visitor on the
    /// other end of a chat conversation, so this renders UTC rather than guessing one, exactly like a
    /// renderer with no zone information is instructed to elsewhere in this codebase.</summary>
    private static string DescribeRange(DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        $"{startsAt:yyyy-MM-dd HH:mm} UTC - {endsAt:HH:mm} UTC";
}
