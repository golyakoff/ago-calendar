using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`ago-root#363`: the assertion surface for <c>RegisterTenantHandlerTests</c> - what got
/// written, in one transaction, or nothing at all if the handler returned early. Same
/// records-what-it-was-asked shape <c>AccessControlFakes</c> already established for this project's
/// other fakes (testing.md: hand-written, not a mocking framework).</summary>
internal sealed class FakeTenantProvisioningStore : ITenantProvisioningStore
{
    public (Tenant Tenant, Role Role, Operator Operator)? Registered { get; private set; }

    public Task RegisterAsync(Tenant tenant, Role role, Operator @operator, CancellationToken cancellationToken)
    {
        Registered = (tenant, role, @operator);
        return Task.CompletedTask;
    }
}
