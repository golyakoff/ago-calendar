using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`'s own "rotate without downtime" Done-when: replaces a tenant's credential, keeping the
/// outgoing one valid for <see cref="OverlapWindow"/> more via <see cref="ChatModuleRegistration.Rotate"/>.
///
/// <para><b><see cref="OverlapWindow"/>: ten minutes, an implementer's-call bound, not a measured
/// one</b> - the same honesty <see cref="EnabledModule.MaxTriggerWords"/>'s own remarks give for its
/// bound in `Ago.Chat.*`. It has to outlast the slowest realistic leg of the rotation itself
/// (`Ago.Chat.*` switching which credential it mints with, plus any request already in flight when
/// that switch happens) with margin - ten minutes is generous against a call that runs at `adr/0065`'s
/// own "human conversation pace", and short enough that a genuinely leaked old credential is not left
/// usable for long after an operator rotates specifically because it leaked.</para>
///
/// <para><b>Called synchronously from `Ago.Chat.*`'s own rotation handler, before it updates its own
/// row.</b> The ordering is load-bearing, not incidental: if `Ago.Chat.*` started minting with the new
/// credential before this handler had committed it here, calls signed in that gap would carry a
/// credential this deployment does not recognise yet - the exact downtime the Done-when asks to avoid,
/// worse than doing nothing. Module-first, chat-second is why - see this repository's own report for
/// the full argument against the alternative (an outbox-dispatched, eventually-consistent rotation).</para>
/// </summary>
public sealed class RotateChatModuleCredentialHandler(
    IChatModuleRegistrationRepository registrations, IClock clock)
{
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMinutes(10);

    public async Task<Result> HandleAsync(RotateChatModuleCredential command, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(command.TenantId);

        var existing = await registrations.GetByTenantIdAsync(tenantId, cancellationToken);
        if (existing is null)
        {
            return ChatModuleRegistrationErrors.NotFound();
        }

        ChatModuleCredential newCredential;
        try
        {
            newCredential = new ChatModuleCredential(command.NewCredential);
        }
        catch (ArgumentException ex)
        {
            return ChatModuleRegistrationErrors.InvalidCredential(ex.Message);
        }

        var rotated = existing.Rotate(newCredential, clock.UtcNow, OverlapWindow);
        await registrations.UpdateAsync(rotated, cancellationToken);
        return Result.Success();
    }
}
