using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ChatModuleRegistration;

namespace Ago.Calendar.Api.ChatModule;

/// <summary>
/// `22-11`: the generic provisioning surface `adr/0065`'s registry needed all along -
/// "site X has module K enabled" (`Ago.Chat.Domain.EnabledModule`) had no way to make that true on
/// this side until now. Same route family as <see cref="ChatModuleTaskEndpoints"/>
/// (`/api/v1/module-registrations`, not `/api/v1/module-tasks`), same server-to-server,
/// outside-any-CORS-policy shape, and the same <c>AllowAnonymous()</c> + hand-rolled header check
/// this class's own sibling already established - see that class's own remarks for why a CORS policy
/// is not needed here either.
///
/// <para><b>A different header, a different check, from <see cref="ChatModuleTaskEndpoints"/>'s own
/// <c>X-Ago-Module-Credential</c>.</b> <c>X-Ago-Module-Provisioning-Secret</c> proves this call is from
/// `Ago.Chat.*`'s own deployment, not from a specific already-registered site - the fact that does not
/// exist yet the first time a site is ever registered. See
/// <see cref="IModuleProvisioningAuthenticator"/>'s own remarks for the full argument against reusing
/// the signed-assertion format instead.</para>
///
/// <para><b>`PUT` creates, `POST .../rotate` rotates, `DELETE` revokes, `GET` reports status for
/// reconciliation.</b> Rotate is not folded into `PUT` as an upsert: `RotateChatModuleCredentialHandler`
/// needs the *existing* row to build the grace-period previous credential
/// (<see cref="Ago.Calendar.Domain.ChatModuleRegistration.Rotate"/>'s own remarks), so "there is no row
/// yet" and "there is a row and I want a new secret for it" are two different requests with two
/// different bodies of business logic, not one idempotent replace - the same reasoning that keeps
/// `EnableModuleForSiteHandler` and a hypothetical rotate handler apart on `Ago.Chat.*`'s own
/// side.</para>
/// </summary>
public static class ModuleRegistrationEndpoints
{
    private const string ProvisioningSecretHeaderName = "X-Ago-Module-Provisioning-Secret";

    public static IEndpointRouteBuilder MapModuleRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/module-registrations").AllowAnonymous();

        group.MapPut("/{tenantId:guid}", HandleRegisterAsync).WithName("RegisterChatModule");
        group.MapPost("/{tenantId:guid}/rotate", HandleRotateAsync).WithName("RotateChatModuleCredential");
        group.MapDelete("/{tenantId:guid}", HandleRevokeAsync).WithName("RevokeChatModuleRegistration");
        group.MapGet("/{tenantId:guid}", HandleGetStatusAsync).WithName("GetChatModuleRegistrationStatus");

        return app;
    }

    private static async Task<IResult> HandleRegisterAsync(
        Guid tenantId,
        RegisterChatModuleRequest request,
        RegisterChatModuleHandler handler,
        IModuleProvisioningAuthenticator authenticator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        if (!authenticator.Authenticate(httpContext.Request.Headers[ProvisioningSecretHeaderName]))
        {
            return Results.Unauthorized();
        }

        var result = await handler.HandleAsync(new RegisterChatModule(tenantId, request.Credential), cancellationToken);
        return result.IsSuccess ? Results.Ok() : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleRotateAsync(
        Guid tenantId,
        RotateChatModuleCredentialRequest request,
        RotateChatModuleCredentialHandler handler,
        IModuleProvisioningAuthenticator authenticator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        if (!authenticator.Authenticate(httpContext.Request.Headers[ProvisioningSecretHeaderName]))
        {
            return Results.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new RotateChatModuleCredential(tenantId, request.NewCredential), cancellationToken);
        return result.IsSuccess ? Results.Ok() : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleRevokeAsync(
        Guid tenantId,
        RevokeChatModuleRegistrationHandler handler,
        IModuleProvisioningAuthenticator authenticator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!authenticator.Authenticate(httpContext.Request.Headers[ProvisioningSecretHeaderName]))
        {
            return Results.Unauthorized();
        }

        var result = await handler.HandleAsync(new RevokeChatModuleRegistration(tenantId), cancellationToken);
        return result.IsSuccess ? Results.Ok() : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleGetStatusAsync(
        Guid tenantId,
        GetChatModuleRegistrationStatusHandler handler,
        IModuleProvisioningAuthenticator authenticator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!authenticator.Authenticate(httpContext.Request.Headers[ProvisioningSecretHeaderName]))
        {
            return Results.Unauthorized();
        }

        var status = await handler.HandleAsync(new GetChatModuleRegistrationStatus(tenantId), cancellationToken);
        return Results.Ok(new ChatModuleRegistrationStatusResponse(
            status.Exists, status.Exists ? status.RegisteredAt : null, status.HasCredentialInGracePeriod));
    }

    /// <param name="Credential">Never echoed back - the same "a secret is accepted, never returned"
    /// hygiene <c>Ago.Chat.Api.Modules.ModuleEndpoints.EnableModuleRequest.Credential</c>'s own remarks
    /// describe for its sibling.</param>
    public sealed record RegisterChatModuleRequest(string Credential);

    public sealed record RotateChatModuleCredentialRequest(string NewCredential);

    public sealed record ChatModuleRegistrationStatusResponse(
        bool Exists, DateTimeOffset? RegisteredAt, bool HasCredentialInGracePeriod);
}
