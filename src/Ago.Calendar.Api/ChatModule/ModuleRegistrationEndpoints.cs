using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Ago.Calendar.Application.UseCases.TenantErasure;
using Ago.Calendar.Domain;

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
        // `22-30`: a distinct route and a distinct verb from the revoke above - deleting the
        // registration (this route family's own `DELETE /{tenantId}`) stops a credential from
        // authenticating; this one deletes the tenant's own data and everything under it. Folding
        // the two together would make one HTTP call mean two irreversible things, one of them a
        // credential's own lifecycle and the other a person's data - the same "one call, one fact"
        // reasoning this route family already keeps rotate and revoke apart for (this file's own
        // opening remarks).
        group.MapDelete("/{tenantId:guid}/tenant-data", HandleEraseAsync).WithName("EraseChatModuleTenantData");

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

        var result = await handler.HandleAsync(
            new RegisterChatModule(tenantId, request.Credential, request.DisplayName), cancellationToken);
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

    /// <summary>
    /// `22-30`: "an operation, authenticated the way `22-11`'s registration calls already are" - the
    /// backlog item's own words for why this reuses the provisioning-secret check above rather than
    /// <c>ChatModuleTaskEndpoints</c>'s per-site signed credential. That choice is deliberate and
    /// stated here rather than left to be inferred: the whole reason this item exists is a tenant
    /// whose per-site credential may have been revoked, may have lapsed, or may never have been
    /// provisioned in the first place - the very cases a per-site credential cannot reach. The
    /// deployment-wide provisioning secret is what chat still holds regardless of any one tenant's
    /// own registration state, which is what lets this endpoint answer "erase" for exactly the
    /// tenants a per-site credential could not authenticate a call for.
    ///
    /// <para>No <see cref="IChatModuleRegistrationRepository"/> lookup here, unlike every other
    /// handler in this class - deliberately: whether a <c>chat_module_registrations</c> row exists is
    /// irrelevant to whether a <see cref="Tenant"/> row (and everything under it) does, and gating
    /// this call on the former would refuse exactly the revoked-registration case this item exists to
    /// close.</para>
    /// </summary>
    private static async Task<IResult> HandleEraseAsync(
        Guid tenantId,
        EraseTenantDataHandler handler,
        IModuleProvisioningAuthenticator authenticator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!authenticator.Authenticate(httpContext.Request.Headers[ProvisioningSecretHeaderName]))
        {
            return Results.Unauthorized();
        }

        var result = await handler.HandleAsync(new EraseTenantData(new TenantId(tenantId)), cancellationToken);
        return Results.Ok(new TenantErasureResponse(result.TenantExisted, result.Confirmed));
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
    /// <param name="DisplayName">`22-17`: an opaque label for the account, used only when this
    /// handler has to provision <see cref="Ago.Calendar.Domain.Tenant"/> itself because no row exists
    /// yet for <paramref name="Credential"/>'s own tenant id - see <c>RegisterChatModuleHandler</c>'s
    /// own remarks.</param>
    public sealed record RegisterChatModuleRequest(string Credential, string? DisplayName = null);

    public sealed record RotateChatModuleCredentialRequest(string NewCredential);

    public sealed record ChatModuleRegistrationStatusResponse(
        bool Exists, DateTimeOffset? RegisteredAt, bool HasCredentialInGracePeriod);

    /// <summary>`22-30`/`adr/0149` rule 2: the module's own proof, on the wire - see
    /// <see cref="Application.Abstractions.TenantErasureResult"/>'s own remarks for what each field
    /// means and why <see cref="Confirmed"/>, not <see cref="TenantExisted"/>, is the fact a caller
    /// should gate on.</summary>
    public sealed record TenantErasureResponse(bool TenantExisted, bool Confirmed);
}
