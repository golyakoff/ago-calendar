namespace Ago.Calendar.Contracts;

/// <summary>
/// The wire shapes of `20-07`'s chat-module surface - hand-synchronized with the `ago-chat` worker
/// building the other end of this contract, over plain HTTP+JSON (System.Text.Json camelCase, the
/// ASP.NET Core Minimal API default). There is no shared package between the two products (adr/0027,
/// adr/0012 sets no precedent for one) - these types and the ones on the other repository's side are
/// two hand-kept copies of one agreement, which is exactly why field names and casing here matter more
/// than they would inside one product: a typo on either side is a silent contract break with no
/// compiler to catch it.
///
/// <para><b><c>Payload</c> is deliberately <c>object</c>, not a closed union type.</b> The wire shape
/// genuinely varies by <see cref="StepDto.Kind"/> - a <c>choice_list</c> carries a prompt, a
/// <c>date_time_picker</c> carries a prompt and a slot list - and <c>System.Text.Json</c> serializes a
/// property declared as <c>object</c> using the value's own runtime type, which is exactly the
/// per-kind payload records below. The alternative, one record with every field nullable, would let a
/// caller construct a <c>form</c> step carrying <c>slots</c> and the compiler would not object.</para>
/// </summary>
public static class ModuleStepKinds
{
    public const string ChoiceList = "choice_list";
    public const string Form = "form";
    public const string ConfirmationCard = "confirmation_card";
    public const string DateTimePicker = "date_time_picker";

    /// <summary>
    /// `20-09`: Chat's own <c>PrimitiveKinds.VerifiedPhoneForm</c> - a <c>form</c> whose payload is
    /// wire-identical (prompt, field id, field label) but whose reply must carry proof of control over
    /// the phone number, not merely the number itself. Calendar never checks that proof; it only ever
    /// emits this kind (<c>ModuleStepFactory.PhoneForm</c>) and reads the assertion Chat attaches to
    /// the reply (<see cref="ModuleTaskReplyRequest.PhoneVerifiedAt"/>) - the identical "modules fill
    /// primitives in, Chat owns what they mean" split every other kind here already respects, applied
    /// in the direction where this product is the module rather than Chat.
    /// </summary>
    public const string VerifiedPhoneForm = "verified_phone_form";
}

/// <summary>Chat's own <c>MessageAction</c> shape (adr/0061) - <c>value</c> is opaque to Chat and is
/// this product's own id, as a string.</summary>
public sealed record ModuleActionDto(string Label, string Value);

public sealed record StepDto(string Kind, object Payload, IReadOnlyList<ModuleActionDto> Actions);

public sealed record ChoiceListPayload(string Prompt);

/// <param name="FieldId">Echoed back on the reply that answers this step, so a caller does not have
/// to remember which field a bare string reply was for.</param>
public sealed record FormPayload(string Prompt, string FieldId, string FieldLabel);

public sealed record ConfirmationLineDto(string Label, string Value);

public sealed record ConfirmationCardPayload(string Title, IReadOnlyList<ConfirmationLineDto> Lines);

/// <param name="Value">Matches one action's own <see cref="ModuleActionDto.Value"/> - this is
/// enrichment for a rich renderer, never the only source of the option.</param>
/// <param name="StartsAt">ISO-8601 with an explicit offset - always UTC on the wire (CLAUDE.md rule
/// 11).</param>
public sealed record SlotOptionDto(string Value, DateTimeOffset StartsAt, string Label);

public sealed record DateTimePickerPayload(string Prompt, IReadOnlyList<SlotOptionDto> Slots);

/// <param name="ChatTaskId">Chat's own id for this task. Opaque to this product - accepted and never
/// stored.</param>
/// <param name="SiteId">Opaque, same reason.</param>
/// <param name="ConversationId">Opaque, same reason.</param>
/// <param name="TriggerText">What the visitor typed to enter the module.</param>
public sealed record ModuleTaskStartRequest(Guid ChatTaskId, Guid SiteId, Guid ConversationId, string TriggerText);

public sealed record ModuleTaskStartResponse(string ExternalTaskId, StepDto Step, bool Complete);

/// <param name="ChatTaskId">Opaque, accepted and never stored - see <see cref="ModuleTaskStartRequest"/>.</param>
/// <param name="Kind">Echoes the step's own <see cref="StepDto.Kind"/>.</param>
/// <param name="Value">For a choice-shaped step, one of that step's own action values. For a
/// <c>form</c> step, the visitor's raw typed text, unvalidated by Chat.</param>
/// <param name="PhoneVerifiedAt">
/// `20-09`: additive (api-design.md - "new optional fields are fine" within a version), null on every
/// reply predating this item and on every reply that does not answer a <c>verified_phone_form</c> step.
/// When present, Chat's own assertion that it checked a `14-15` <c>ChannelIdentity</c> for this exact
/// visitor and phone before ever sending this request - trusted, not re-derived, the identical
/// service-to-service boundary this wire already accepts for the module task itself (adr/0077:
/// "authenticity is checked; the deeper claim is trusted"). Threaded to
/// <c>Ago.Calendar.Application.UseCases.BookEvent.BookEvent.PhoneVerifiedAt</c> unchanged; see that
/// type and <c>BookEventHandler</c>'s own remarks for what refuses a claim carrying none.
/// </param>
public sealed record ModuleTaskReplyRequest(Guid ChatTaskId, string Kind, string Value, DateTimeOffset? PhoneVerifiedAt = null);

/// <param name="Step">Null exactly when <see cref="Complete"/> is true - no further reply is
/// expected.</param>
public sealed record ModuleTaskReplyResponse(StepDto? Step, bool Complete);
