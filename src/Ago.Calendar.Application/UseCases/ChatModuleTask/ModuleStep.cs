namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// AGO Calendar's own view of `adr/0065`'s closed primitive vocabulary - a choice list, a form, a
/// confirmation card, a date-and-time picker - the same four kinds <c>PublicBookingContracts</c>' own
/// doc comment already anticipated ("a message carries a kind, an opaque payload and actions").
///
/// <para><b>Application-shaped, not wire-shaped, and that split is deliberate.</b> The actual wire
/// DTOs (<c>Ago.Calendar.Contracts.StepDto</c> and its per-kind payload records) use exactly the field
/// names and casing the `ago-chat` worker's HTTP client expects - <c>chatTaskId</c>-style camelCase,
/// a <c>payload</c> object shaped differently per <c>kind</c>. Building those directly in a handler
/// would mean Application knows the wire's exact JSON shape, which is the same reasoning
/// <c>GetBookingSurfaceHandler</c>/<c>PublicBookingEndpoints</c> already split on: the handler returns
/// a plain result, and the endpoint (`Ago.Calendar.Api.ChatModule.ChatModuleTaskEndpoints`) is where
/// it becomes a wire DTO. <c>BookingConfirmedMapper</c> looks like a counter-example - Application
/// building a <c>Contracts</c> type directly - but that is a different kind of boundary: an outbox
/// payload is something Application must produce *inside the write transaction* (CLAUDE.md rule 4),
/// so there is no later layer for that translation to live in. An HTTP response has no such
/// constraint, so it follows the query-handler split instead.</para>
/// </summary>
public enum ModuleStepKind
{
    ChoiceList,
    Form,
    ConfirmationCard,
    DateTimePicker,

    /// <summary>`20-09`: wire-identical to <see cref="Form"/> - see <c>Ago.Calendar.Contracts.ModuleStepKinds.VerifiedPhoneForm</c>'s
    /// own remarks for why this is a distinct kind rather than a flag.</summary>
    VerifiedPhoneForm,
}

/// <summary>Chat's own <c>MessageAction</c> shape (label, opaque value) - <c>value</c> is meaningful
/// only to whichever handler produced this step and is echoed back verbatim on the next reply.</summary>
public readonly record struct ModuleAction(string Label, string Value);

public readonly record struct ConfirmationLine(string Label, string Value);

/// <param name="Value">Matches one action's own <see cref="ModuleAction.Value"/> - the enrichment for
/// a rich renderer, never the only source of the option (see backlog: "payload.slots can be skipped"
/// if short on time; this item does not skip it).</param>
public readonly record struct SlotOption(string Value, DateTimeOffset StartsAt, string Label);

/// <summary>
/// One step. Only the fields <see cref="Kind"/> calls for are set; the rest are null/empty by
/// construction through the factory methods below, which is what keeps a caller from building a
/// nonsensical step (a <c>form</c> with slots, say) by simply not offering a constructor that could.
/// </summary>
public sealed record ModuleStep(
    ModuleStepKind Kind,
    string? Prompt,
    string? FieldId,
    string? FieldLabel,
    string? ConfirmationTitle,
    IReadOnlyList<ConfirmationLine>? ConfirmationLines,
    IReadOnlyList<SlotOption>? Slots,
    IReadOnlyList<ModuleAction> Actions)
{
    public static ModuleStep ChoiceListStep(string prompt, IReadOnlyList<ModuleAction> actions) =>
        new(ModuleStepKind.ChoiceList, prompt, null, null, null, null, null, actions);

    public static ModuleStep FormStep(string prompt, string fieldId, string fieldLabel) =>
        new(ModuleStepKind.Form, prompt, fieldId, fieldLabel, null, null, null, []);

    /// <summary>`20-09`: same payload shape as <see cref="FormStep"/> - only <see cref="Kind"/>
    /// differs, which is the whole signal.</summary>
    public static ModuleStep VerifiedPhoneFormStep(string prompt, string fieldId, string fieldLabel) =>
        new(ModuleStepKind.VerifiedPhoneForm, prompt, fieldId, fieldLabel, null, null, null, []);

    public static ModuleStep ConfirmationStep(string title, IReadOnlyList<ConfirmationLine> lines) =>
        new(ModuleStepKind.ConfirmationCard, null, null, null, title, lines, null, []);

    public static ModuleStep DateTimePickerStep(
        string prompt, IReadOnlyList<SlotOption> slots, IReadOnlyList<ModuleAction> actions) =>
        new(ModuleStepKind.DateTimePicker, prompt, null, null, null, null, slots, actions);
}
