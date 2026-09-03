using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Provisioning;

/// <param name="PublicKey">Chosen by whoever provisions the tenant, not generated - see
/// <see cref="TenantPublicKey"/>.</param>
/// <param name="ExternalSubjectId">The Keycloak <c>sub</c> the first operator signs in as, when it is
/// already known - true for dev/test seeding, where the realm is provisioned by import so the id is
/// deterministic. <see langword="null"/> for a real tenant (`ago-root#363`), whose owner has not
/// signed in yet: exactly one of this and <see cref="OwnerEmail"/> must be supplied.</param>
/// <param name="OwnerEmail">`ago-root#363`/adr/0088: the account owner's real address, for a
/// provisioning caller that does not know their Keycloak <c>sub</c> in advance - the normal case for
/// a real client. Reuses adr/0088's own invite-by-email mechanism one caller earlier than that ADR
/// itself needed it: the operator row is created invited (<see cref="Operator.InvitedEmail"/> set,
/// <see cref="Operator.ExternalSubjectId"/> null), and <c>OperatorIdentityClaimsTransformation</c>'s
/// existing email fallback links it on the owner's first sign-in, exactly as it already does for a
/// colleague <c>InviteOperatorHandler</c> invites. No second linking mechanism, no Keycloak admin
/// call - see this item's own report for why that was the deciding argument over a service-account
/// shape.</param>
/// <param name="TenantId">`22-03`/adr/0093: the account id, when the caller already has one -
/// AGO Chat calls the same value <c>SiteId</c>. <see langword="null"/> keeps this handler's older
/// behaviour of minting its own id, which is what the standalone door adr/0093 deliberately left open
/// (`RegisterTenantHandler` and the `20-27` provisioner both still work with nothing supplied here).
/// Accepting a value is not the same as trusting it: nothing downstream can tell a genuine account id
/// from any other GUID - the only defence today is the store's own unique key on <c>id</c>, which
/// turns a repeat into a refusal (see <c>ProvisionerRunner.IsUniqueViolation</c>) rather than a silent
/// second row.</param>
public readonly record struct RegisterTenant(
    string Name,
    string PublicKey,
    string OperatorDisplayName,
    string? ExternalSubjectId,
    IReadOnlyList<string> AllowedOrigins,
    string? OwnerEmail = null,
    TenantId? TenantId = null);

/// <summary>
/// Creates a tenant, the v1 <see cref="Role.OperatorRoleName"/> role and its first operator, in one
/// transaction.
///
/// <para><b>Not self-registration.</b> AGO Chat has `10-02`'s real signup flow; this is a
/// provisioning step with a named subject, and the route in front of it is not exposed in Production
/// (<c>DevProvisioningEndpoints</c>). Building a public signup for this product is its own item with
/// its own abuse questions, and doing it as a side effect of the console item would be inventing a
/// product decision in the wrong place.</para>
///
/// <para><b>The seeded role grants the whole catalogue, including
/// <see cref="Permission.CalendarConfigure"/></b> - <see cref="Role.SeedOperatorRole"/>'s own remarks
/// argue why: in a small business one person is the tenant, the operator and the only worker, so a v1
/// that could not configure its own calendar from the operator login would be unusable by its own
/// target customer.</para>
/// </summary>
public sealed class RegisterTenantHandler(
    ITenantProvisioningStore store,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RegisteredTenant>> HandleAsync(
        RegisterTenant command, CancellationToken cancellationToken)
    {
        // `ago-root#363`: the two ways this handler now learns who the account owner is are mutually
        // exclusive by construction, not by convention - Operator.Create would happily accept both or
        // neither (its own remarks say so explicitly), so the refusal has to live here, before either
        // branch runs, or a caller passing both would silently get whichever branch the code happened
        // to check first.
        var hasSubject = !string.IsNullOrWhiteSpace(command.ExternalSubjectId);
        var hasOwnerEmail = !string.IsNullOrWhiteSpace(command.OwnerEmail);
        if (hasSubject == hasOwnerEmail)
        {
            return new Error(
                "provisioning.invalid",
                "Exactly one of ExternalSubjectId or OwnerEmail must be supplied - the former for a "
                + "subject already known (dev/test seeding), the latter for a real tenant whose owner "
                + "signs in later and links by email (adr/0088's mechanism, reused for the account "
                + "owner).");
        }

        var now = clock.UtcNow;

        Tenant tenant;
        Role role;
        Operator @operator;
        try
        {
            // `22-03`/adr/0093: provenance moves, nothing else does - a caller-supplied id (the
            // account id) is used as-is; otherwise this handler still mints its own, exactly as it
            // did before this item, so the standalone door stays open.
            tenant = Tenant.Register(
                command.TenantId ?? new TenantId(idGenerator.NewId(now)),
                command.Name,
                new TenantPublicKey(command.PublicKey ?? string.Empty),
                now,
                command.AllowedOrigins ?? []);

            role = Role.SeedOperatorRole(new RoleId(idGenerator.NewId(now)), tenant.Id);

            // `20-12`: the tenant's first operator is its account owner - the only caller in this
            // codebase that passes isAccountOwner: true, matching Operator.IsAccountOwner's own
            // remarks on why that is a fact about how the row came to exist, not a state anything
            // else should set.
            //
            // `ago-root#363`: when only OwnerEmail is known, the owner is created exactly the way
            // InviteOperatorHandler creates a colleague - invited, unlinked - so the same claims-
            // transformation fallback that links a colleague on their first sign-in links the owner
            // too. Nothing downstream needs to know which path provisioned this row.
            @operator = hasSubject
                ? Operator.Create(
                    new OperatorId(idGenerator.NewId(now)),
                    tenant.Id,
                    command.OperatorDisplayName,
                    command.ExternalSubjectId,
                    isAccountOwner: true)
                : Operator.Create(
                    new OperatorId(idGenerator.NewId(now)),
                    tenant.Id,
                    command.OperatorDisplayName,
                    externalSubjectId: null,
                    isAccountOwner: true,
                    invitedEmail: new InvitedEmail(command.OwnerEmail!));

            // Takes the whole Role, not a RoleId: an id cannot answer "does this role belong to my
            // tenant", so the check would have to move to this caller - which is exactly what
            // Operator.Grant's own remarks refuse.
            @operator.Grant(role);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return new Error("provisioning.invalid", exception.Message);
        }

        await store.RegisterAsync(tenant, role, @operator, cancellationToken);

        return Result<RegisteredTenant>.Success(
            new RegisteredTenant(tenant.Id, @operator.Id, tenant.PublicKey));
    }
}

public readonly record struct RegisteredTenant(TenantId TenantId, OperatorId OperatorId, TenantPublicKey PublicKey);
