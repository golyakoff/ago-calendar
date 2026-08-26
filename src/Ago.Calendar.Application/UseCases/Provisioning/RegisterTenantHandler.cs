using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Provisioning;

/// <param name="PublicKey">Chosen by whoever provisions the tenant, not generated - see
/// <see cref="TenantPublicKey"/>.</param>
/// <param name="ExternalSubjectId">The Keycloak <c>sub</c> the first operator signs in as. Supplied,
/// never invented: this product does not talk to Keycloak's admin API, and adr/0022's own consequence
/// section says the realm is provisioned by import so the id is deterministic.</param>
public readonly record struct RegisterTenant(
    string Name,
    string PublicKey,
    string OperatorDisplayName,
    string ExternalSubjectId,
    IReadOnlyList<string> AllowedOrigins);

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
        var now = clock.UtcNow;

        Tenant tenant;
        Role role;
        Operator @operator;
        try
        {
            tenant = Tenant.Register(
                new TenantId(idGenerator.NewId(now)),
                command.Name,
                new TenantPublicKey(command.PublicKey ?? string.Empty),
                now,
                command.AllowedOrigins ?? []);

            role = Role.SeedOperatorRole(new RoleId(idGenerator.NewId(now)), tenant.Id);

            @operator = Operator.Create(
                new OperatorId(idGenerator.NewId(now)),
                tenant.Id,
                command.OperatorDisplayName,
                command.ExternalSubjectId);

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
