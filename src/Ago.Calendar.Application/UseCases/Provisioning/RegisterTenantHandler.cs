using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Provisioning;

/// <param name="TenantId">`22-03`/adr/0093: the account id, when the caller already has one - AGO
/// Chat calls the same value <c>SiteId</c>. <see langword="null"/> keeps this handler's older
/// behaviour of minting its own id, which is what the standalone door adr/0093 deliberately left open
/// (this handler and the `20-27` provisioner both still work with nothing supplied here). Accepting a
/// value is not the same as trusting it: nothing downstream can tell a genuine account id from any
/// other GUID - the only defence today is the store's own unique key on <c>id</c>, which turns a
/// repeat into a refusal (see <c>ProvisionerRunner.IsUniqueViolation</c>) rather than a silent second
/// row.</param>
public readonly record struct RegisterTenant(
    string Name,
    string PublicKey,
    IReadOnlyList<string> AllowedOrigins,
    TenantId? TenantId = null);

/// <summary>
/// Creates a tenant. One aggregate, one write - see this class's own remarks below for why this
/// handler used to be three aggregates in one transaction and no longer is.
///
/// <para><b>`22-05`/`adr/0093`: no operator, no role, created here any more.</b> Before this item, this
/// handler also minted the tenant's first `Operator` (the account owner) and its seeded `Role`,
/// because AGO Calendar held its own identity. It does not any more - there is no local `operators`
/// table to put a row in, and the account owner's `calendar:configure` permission arrives through the
/// projection <c>RoleAssignmentsChanged</c> replicates, published by whichever `ago-chat` handler
/// created that person's account-side operator row (`RegisterSiteHandler`, `MintDemoTenantHandler`).
/// Provisioning a calendar tenant is now exactly what its name says: registering the tenant, nothing
/// riding along with it.</para>
///
/// <para><b>Not self-registration.</b> AGO Chat has `10-02`'s real signup flow; this is a provisioning
/// step, and the route in front of it is not exposed in Production
/// (<c>DevProvisioningEndpoints</c>). Building a public signup for this product is its own item with
/// its own abuse questions, and doing it as a side effect of this item would be inventing a product
/// decision in the wrong place.</para>
/// </summary>
public sealed class RegisterTenantHandler(
    ITenantRepository tenants,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RegisteredTenant>> HandleAsync(
        RegisterTenant command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        Tenant tenant;
        try
        {
            // `22-03`/adr/0093: provenance moves, nothing else does - a caller-supplied id (the
            // account id) is used as-is; otherwise this handler still mints its own, exactly as it
            // did before that item, so the standalone door stays open.
            tenant = Tenant.Register(
                command.TenantId ?? new TenantId(idGenerator.NewId(now)),
                command.Name,
                new TenantPublicKey(command.PublicKey ?? string.Empty),
                now,
                command.AllowedOrigins ?? []);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return new Error("provisioning.invalid", exception.Message);
        }

        await tenants.AddAsync(tenant, cancellationToken);

        return Result<RegisteredTenant>.Success(new RegisteredTenant(tenant.Id, tenant.PublicKey));
    }
}

public readonly record struct RegisteredTenant(TenantId TenantId, TenantPublicKey PublicKey);
