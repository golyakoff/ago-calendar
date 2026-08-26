using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Api.Provisioning;

/// <summary>
/// Creates a tenant, its seeded role and its first operator - <b>outside Production only</b>.
///
/// <para><b>Why this exists.</b> `20-01` said the provisioning transaction "belongs to `20-06`", and
/// `20-06`'s own Done-when needs a tenant that exists before a console can configure it. Without this
/// route the only way to get one is to write SQL by hand, which is not something a runbook should
/// ask of anybody.</para>
///
/// <para><b>Why it is not a signup, and why the gate is the environment rather than a permission.</b>
/// A public "create a tenant" endpoint is a real product decision with real abuse questions - AGO
/// Chat has an item for it (`10-02`) and this product does not. A permission gate would be the wrong
/// answer here anyway: the caller who needs this has no operator row yet, so there is nothing to
/// check a permission against. Mapping it only when the environment is not Production is the same
/// call `1-06`'s <c>POST /dev/operator-session</c> made in AGO Chat - with the difference, which
/// matters, that this one creates a tenant rather than handing out an identity, so no part of it is
/// a trust-model shortcut that a later item has to delete outright.</para>
/// </summary>
public static class DevProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapDevProvisioningEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/dev/tenants", HandleRegisterAsync)
            .WithName("RegisterTenant")
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleRegisterAsync(
        RegisterTenantRequest request,
        RegisterTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new RegisterTenant(
                request.Name,
                request.PublicKey,
                request.OperatorDisplayName,
                request.ExternalSubjectId,
                request.AllowedOrigins ?? []),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var registered = result.Value;
        return Results.Ok(new RegisterTenantResponse(
            registered.TenantId.Value, registered.OperatorId.Value, registered.PublicKey.Value));
    }
}
