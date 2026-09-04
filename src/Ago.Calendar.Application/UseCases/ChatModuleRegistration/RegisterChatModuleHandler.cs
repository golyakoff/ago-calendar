using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

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
///
/// <para><b>`22-17`: a missing <see cref="Tenant"/> is provisioned here, not refused - and this is a
/// real, enumerated widening of what the provisioning secret can do, not a free extension of trust
/// already given.</b> Before this item, a tenant absent from this product's own <c>tenants</c> table
/// made every chat-originated grant fail with <see cref="ChatModuleRegistrationErrors.TenantNotFound"/>,
/// because nothing in Production ever calls <c>RegisterTenantHandler</c> (its own route,
/// <c>DevProvisioningEndpoints</c>, is deliberately not mapped there) - so <b>zero surfaces that create
/// a <see cref="Tenant"/> row were reachable in Production at all</b>. This handler is now one. `adr/0095`
/// enumerates the provisioning secret's own blast radius as "register, rotate or delete the registration
/// for any site the deployment serves" - acting on a row that already exists. A holder of that same
/// secret can now, in addition, <b>bring a new row into existence</b>, for any <see cref="TenantId"/>
/// they choose, with a <see cref="Tenant.Name"/> they also choose - `adr/0098` amends `adr/0095` to say
/// so plainly rather than leave that ADR's own blast-radius section silently wrong. What does not
/// change: the population who could ever exploit this is the identical one `adr/0095` already accepted
/// for the sibling capabilities (whoever holds the deployment's provisioning secret, on the cluster-
/// internal channel `22-18` is the item that closes off from the internet entirely) - and what this
/// item adds on top of that acceptance is <see cref="Tenant.AutoProvisioned"/>, a real, queryable
/// marker distinguishing a row this path minted from one a human registered, which did not exist before
/// and is this item's own answer to "is an auto-provisioned tenant distinguishable from a real one" -
/// yes, now, though nothing yet bounds how many such rows one secret can create; that remains open and
/// is named in `adr/0098` as `22-18`'s own scope to widen, not solved here. <see cref="Tenant.PublicKey"/>
/// is minted from <paramref name="command"/>'s own <see cref="RegisterChatModule.TenantId"/> rather than
/// asked of the caller: nothing about a chat-driven-only tenant needs the public, chosen key a standalone
/// embed would (<see cref="TenantPublicKey"/>'s own remarks - "chosen, not generated" - describes that
/// door, still open, still unused here), and inventing a value chat has no reason to hold would be a
/// second fact for the two sides to agree on for nothing this call needs.</para>
/// </summary>
public sealed class RegisterChatModuleHandler(
    ITenantRepository tenants, IChatModuleRegistrationRepository registrations, IClock clock,
    ILogger<RegisterChatModuleHandler> logger)
{
    public async Task<Result> HandleAsync(RegisterChatModule command, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(command.TenantId);
        var now = clock.UtcNow;

        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            var displayName = string.IsNullOrWhiteSpace(command.DisplayName)
                ? $"Tenant {command.TenantId}"
                : command.DisplayName;
            var publicKey = new TenantPublicKey($"chat-{command.TenantId:N}");

            try
            {
                tenant = Tenant.AutoProvisionForChatModule(tenantId, displayName, publicKey, now);
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                return ChatModuleRegistrationErrors.TenantProvisioningFailed(exception.Message);
            }

            await tenants.AddAsync(tenant, cancellationToken);

            // `22-17`: the only audit trail this call gets today - adr/0095's own "no audit log of
            // provisioning calls" gap, named again here rather than silently inherited, because this
            // specific capability (minting a brand-new Tenant row, not merely acting on one that
            // already existed) is not on that ADR's own enumerated list and needed one. Never the
            // provisioning secret, never the credential - a tenant id and the name the caller chose,
            // the same "a site id is not a credential" judgement HmacModuleCallCredentialValidator's
            // own remarks (adr/0099) already make for its sibling log line.
            logger.LogWarning(
                "Chat module registration auto-provisioned a new tenant {TenantId} ({DisplayName}) - " +
                "no prior Tenant row existed for this id.",
                tenantId.Value, displayName);
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
