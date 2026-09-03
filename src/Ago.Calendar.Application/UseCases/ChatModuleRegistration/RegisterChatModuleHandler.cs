using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>
/// `22-11`: registers "tenant X's chat-originated calls are proven by this credential" - the write
/// this item's own backlog item found missing entirely. Driven by `Ago.Chat.*`'s own
/// `EnableModuleForSiteHandler`, over HTTP, synchronously - not through the outbox. See this
/// repository's own report for why: rotation's own "no downtime" claim needs the module side to
/// confirm a credential change *before* `Ago.Chat.*` starts minting calls with it, which an
/// eventually-consistent outbox dispatch cannot promise, and registration follows the identical shape
/// for the same reason (a fresh `EnabledModule` row on Chat's side with no matching registration here
/// yet would make the module "callable" a moment before it can actually answer a call) - so this call
/// happens first, and `Ago.Chat.*` only persists its own row once this handler returns success. This is
/// the classic two-phase, no-distributed-transaction shape rule 4 asks for restated for an RPC rather
/// than a fact-publication: `Ago.Chat.*`'s own `IModuleGateway.StartTaskAsync` already calls this
/// deployment synchronously from an Application handler for the identical "ask another system and use
/// the answer" reason, and this call is that same shape, not a state-change notification.
///
/// <para><b>Existence-checked, not exception-caught.</b> A tenant that already has a row is refused by
/// reading first, not by attempting <see cref="IChatModuleRegistrationRepository.AddAsync"/> and
/// catching a unique-violation - catching a Postgres-specific exception here would mean Application
/// knowing what database backs it, the exact leak the dependency rule forbids (rule 2). This accepts a
/// narrow, honestly-stated race: two concurrent registration attempts for the same tenant could both
/// pass the existence check and one would then fail at the repository with an unhandled exception. Not
/// a realistic threat for a low-volume, single-caller admin path - see this handler's own report for
/// why closing it with a database-specific catch was judged not worth the layering cost.</para>
/// </summary>
public sealed class RegisterChatModuleHandler(
    ITenantRepository tenants, IChatModuleRegistrationRepository registrations, IClock clock)
{
    public async Task<Result> HandleAsync(RegisterChatModule command, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(command.TenantId);

        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return ChatModuleRegistrationErrors.TenantNotFound();
        }

        var existing = await registrations.GetByTenantIdAsync(tenantId, cancellationToken);
        if (existing is not null)
        {
            return ChatModuleRegistrationErrors.AlreadyRegistered();
        }

        ChatModuleCredential credential;
        try
        {
            credential = new ChatModuleCredential(command.Credential);
        }
        catch (ArgumentException ex)
        {
            return ChatModuleRegistrationErrors.InvalidCredential(ex.Message);
        }

        var registration = Domain.ChatModuleRegistration.Register(tenantId, credential, clock.UtcNow);
        await registrations.AddAsync(registration, cancellationToken);
        return Result.Success();
    }
}
