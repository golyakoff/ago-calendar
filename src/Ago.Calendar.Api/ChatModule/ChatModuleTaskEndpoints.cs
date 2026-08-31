using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.ChatModuleTask;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Api.ChatModule;

/// <summary>
/// `20-07`'s wire contract with `Ago.Chat.*` - the two endpoints a chat conversation drives a booking
/// through, hand-synchronized with the `ago-chat` worker building the other side (see
/// <c>Ago.Calendar.Contracts.ModuleTaskContracts</c> for the shared field-level detail).
///
/// <para><b>Server-to-server, not widget-facing, and deliberately outside
/// <c>TenantOriginCorsPolicyProvider</c>'s two layers.</b> Nothing here checks an <c>Origin</c> header
/// at all - not because the check was removed, but because it was never wired: <c>Program.cs</c>'s
/// <c>app.UseCors()</c> middleware only acts on a request that carries an <c>Origin</c> header, and a
/// server calling another server does not send one. A browser could still call these endpoints
/// directly, and since CORS is a browser-side reading restriction rather than an authorization
/// mechanism (api-design.md), that would not let a page read a cross-origin response anyway - so
/// leaving them outside the tenant-origin policy costs nothing and adding one would answer a question
/// nobody is asking. What is genuinely missing, and named here rather than solved: <b>no
/// service-to-service authentication exists in either direction yet</b>. Neither this repository nor
/// `ago-chat` has a precedent for one, and inventing an ad hoc scheme for this item alone was
/// explicitly out of scope - see this item's own report.</para>
///
/// <para><b>200, not 201, on the <c>POST</c> that starts a task.</b> api-design.md's default is
/// <c>201</c> with a <c>Location</c> for a creating <c>POST</c>. This route deviates on purpose, the
/// same call <c>BookingEndpoints</c> already made and for a related reason: the wire contract here is
/// fixed by hand-agreement with another repository's own HTTP client, which reads
/// <c>{ externalTaskId, step, complete }</c> from a <c>200</c> body - and there is no
/// <c>GET</c> this product serves for a module task that a <c>Location</c> header could honestly point
/// at.</para>
/// </summary>
public static class ChatModuleTaskEndpoints
{
    public static IEndpointRouteBuilder MapChatModuleTaskEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/module-tasks").AllowAnonymous();

        group.MapPost("/", HandleStartAsync).WithName("StartModuleTask");
        group.MapPost("/{externalTaskId}/replies", HandleReplyAsync).WithName("ReplyToModuleTask");

        return app;
    }

    private static async Task<IResult> HandleStartAsync(
        ModuleTaskStartRequest request,
        StartModuleTaskHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new StartModuleTask(request.ChatTaskId, request.SiteId, request.ConversationId, request.TriggerText),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var started = result.Value;
        return Results.Ok(new ModuleTaskStartResponse(
            started.ExternalTaskId, ToStepDto(started.Step), started.Complete));
    }

    private static async Task<IResult> HandleReplyAsync(
        string externalTaskId,
        ModuleTaskReplyRequest request,
        ReplyToModuleTaskHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new ReplyToModuleTask(externalTaskId, request.ChatTaskId, request.Kind, request.Value, request.PhoneVerifiedAt),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var replied = result.Value;
        return Results.Ok(new ModuleTaskReplyResponse(
            replied.Step is { } step ? ToStepDto(step) : null, replied.Complete));
    }

    /// <summary>Application's <see cref="ModuleStep"/> - a plain result, ignorant of wire casing and
    /// of <c>System.Text.Json</c> - becomes the wire's own per-kind payload shape. See
    /// <see cref="ModuleStep"/>'s own remarks for why this translation lives here and not in the
    /// handler that built the step.</summary>
    private static StepDto ToStepDto(ModuleStep step) => step.Kind switch
    {
        ModuleStepKind.ChoiceList => new StepDto(
            ModuleStepKinds.ChoiceList,
            new ChoiceListPayload(step.Prompt!),
            ToActionDtos(step.Actions)),

        ModuleStepKind.Form => new StepDto(
            ModuleStepKinds.Form,
            new FormPayload(step.Prompt!, step.FieldId!, step.FieldLabel!),
            []),

        // `20-09`: wire-identical to Form - only Kind differs, which is the whole signal (ModuleStepFactory's
        // own remarks).
        ModuleStepKind.VerifiedPhoneForm => new StepDto(
            ModuleStepKinds.VerifiedPhoneForm,
            new FormPayload(step.Prompt!, step.FieldId!, step.FieldLabel!),
            []),

        ModuleStepKind.ConfirmationCard => new StepDto(
            ModuleStepKinds.ConfirmationCard,
            new ConfirmationCardPayload(
                step.ConfirmationTitle!,
                [.. step.ConfirmationLines!.Select(l => new ConfirmationLineDto(l.Label, l.Value))]),
            []),

        ModuleStepKind.DateTimePicker => new StepDto(
            ModuleStepKinds.DateTimePicker,
            new DateTimePickerPayload(
                step.Prompt!,
                [.. step.Slots!.Select(s => new SlotOptionDto(s.Value, s.StartsAt, s.Label))]),
            ToActionDtos(step.Actions)),

        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "Unknown module step kind."),
    };

    private static IReadOnlyList<ModuleActionDto> ToActionDtos(IReadOnlyList<ModuleAction> actions) =>
        [.. actions.Select(a => new ModuleActionDto(a.Label, a.Value))];
}
