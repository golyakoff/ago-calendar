using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`'s own "revoking refuses subsequent calls" Done-when. Deletion is immediate and total - no
/// grace window, unlike <see cref="RotateChatModuleCredentialHandler"/>: rotation's overlap exists to
/// protect a call in flight when the *legitimate* secret is about to change; revocation exists
/// precisely for the moment a secret should stop working as fast as this deployment can make that
/// true, so a grace window here would undercut the one thing the operation is for. The very next call
/// this deployment answers for the tenant finds no row at all -
/// <see cref="Ago.Calendar.Infrastructure.Postgres.HmacModuleCallCredentialValidator"/> already treats
/// that identically to "never registered" (see its own remarks), so this reuses an existing refusal
/// path rather than adding a new one.
/// </summary>
public sealed class RevokeChatModuleRegistrationHandler(IChatModuleRegistrationRepository registrations)
{
    public async Task<Result> HandleAsync(RevokeChatModuleRegistration command, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(command.TenantId);

        var existing = await registrations.GetByTenantIdAsync(tenantId, cancellationToken);
        if (existing is null)
        {
            return ChatModuleRegistrationErrors.NotFound();
        }

        await registrations.DeleteAsync(tenantId, cancellationToken);
        return Result.Success();
    }
}
