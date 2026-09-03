using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ChatModuleTask;
using Ago.Calendar.Contracts;
using Ago.Platform.Kernel;

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
/// nobody is asking.</para>
///
/// <para><b>`22-02`: every request now carries a signed <c>X-Ago-Module-Credential</c> header</b>,
/// checked by <see cref="IModuleCallCredentialValidator"/> before either handler ever runs - the gap
/// this class's own remarks used to name as genuinely missing. A missing-or-wrong credential is refused
/// with <c>401</c> (still not ASP.NET Core's own authentication pipeline - <c>AllowAnonymous()</c>
/// below is accurate: there is no cookie or bearer-JWT user identity here, only this route's own
/// hand-rolled service-to-service check). <see cref="HandleStartAsync"/> cross-checks the credential's
/// own site id against <see cref="ModuleTaskStartRequest.SiteId"/> - the exact value
/// `docs/backlog/22-02-*` named as "the moment `22-04` makes resolution per-site, the body becomes the
/// tenant selector."</para>
///
/// <para><b>`22-04`: <see cref="HandleReplyAsync"/> now cross-checks too</b>, closing the asymmetry
/// adr/0094 named ("`ChatBookingTask` carries no site id of its own... giving that task a real
/// per-site identity is `22-04`'s job"). Unlike Start, the check does not happen in this endpoint: the
/// credential's own site id is threaded into <see cref="ReplyToModuleTask.CredentialSiteId"/> and
/// checked against the task actually being replied to (<c>ReplyToModuleTaskHandler</c>'s own remarks) -
/// the exact split <c>Ago.Faq.Api.ModuleTasks.ModuleTaskEndpoints</c> already established for its own
/// identical route, because the real check is "does this credential's site own the task", not "does it
/// merely match the request shape", and the task is not loaded until the handler runs.</para>
///
/// <para><b>`22-04`: the credential is now checked against a per-tenant secret</b>
/// (<c>HmacModuleCallCredentialValidator</c>'s own remarks) rather than one shared across this whole
/// deployment - <see cref="IModuleCallCredentialValidator.ValidateAsync"/> reads a database row to do
/// it, which is why the call below is awaited rather than a synchronous method call.</para>
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

    private const string CredentialHeaderName = "X-Ago-Module-Credential";

    private static async Task<IResult> HandleStartAsync(
        ModuleTaskStartRequest request,
        StartModuleTaskHandler handler,
        IModuleCallCredentialValidator credentialValidator,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var auth = await credentialValidator.ValidateAsync(
            httpContext.Request.Headers[CredentialHeaderName], clock.UtcNow, cancellationToken);
        if (!auth.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        // The one place this item's own Done-when bites: a credential valid for one site cannot name
        // another in the body. `22-04` removed the one case that used to leave auth.SiteId null (the
        // accepting-but-warning rollout window) - IsAuthenticated true now always carries a real site
        // id - but the null-conditional stays rather than an assumed-non-null read, so this check
        // degrades safely rather than throwing if that ever stops being true again.
        if (auth.SiteId is { } authenticatedSiteId && authenticatedSiteId != request.SiteId)
        {
            return Results.Unauthorized();
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
        IModuleCallCredentialValidator credentialValidator,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var auth = await credentialValidator.ValidateAsync(
            httpContext.Request.Headers[CredentialHeaderName], clock.UtcNow, cancellationToken);
        if (!auth.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        // `22-04`: the cross-check itself happens inside the handler, against the task actually
        // being replied to - see this class's own remarks.
        var result = await handler.HandleAsync(
            new ReplyToModuleTask(
                externalTaskId, request.ChatTaskId, request.Kind, request.Value, request.PhoneVerifiedAt, auth.SiteId),
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
