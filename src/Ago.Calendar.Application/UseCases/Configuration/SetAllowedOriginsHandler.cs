using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// The editor `5-01` deferred, built here because `20-06`'s Done-when cannot be met without it - see
/// <see cref="SetAllowedOrigins"/>.
///
/// <para><b>The stale-negative question `5-01`/`10-04` left open does not arise, and the reason is
/// worth stating rather than inherited by assumption.</b> `10-04` proved that AGO Chat's per-origin
/// negative cache cannot strand a self-registered site, and left the general case open for
/// "a future feature that lets an origin be freed and immediately reclaimed by a different tenant" -
/// naming an origin editor as the likely trigger. This is that editor. It is safe here because
/// <c>CheckTenantOriginHandler</c> deliberately has no cache at all: the layer-1 answer is read from
/// the table on every preflight, so an origin approved a second ago is approved now and an origin
/// revoked a second ago is revoked now. The cost is one indexed <c>EXISTS</c>; the benefit is that
/// this item does not have to invent an invalidation story for a cache nobody has measured a need
/// for.</para>
/// </summary>
public sealed class SetAllowedOriginsHandler(ITenantRepository tenants, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(SetAllowedOrigins command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var tenant = await tenants.GetByIdAsync(command.TenantId, cancellationToken);
        if (tenant is null)
        {
            return ConfigurationErrors.TenantNotFound(command.TenantId);
        }

        try
        {
            tenant.SetAllowedOrigins(command.Origins ?? []);
        }
        catch (ArgumentException exception)
        {
            // "https://shop.example/booking" is the mistake a real person makes here, and Tenant
            // refuses it rather than silently trimming the path - see its own remarks for why
            // granting more than the tenant asked for is the worse failure.
            return ConfigurationErrors.Invalid(exception.Message);
        }

        await tenants.SaveAsync(tenant, cancellationToken);
        return Result.Success();
    }
}
