using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// Writes a tenant, its seeded operator role and its first operator in <b>one</b> transaction.
///
/// <para><b>The explicit multi-aggregate port <see cref="ITenantRepository"/> predicted.</b> Its own
/// remarks say every mutating method on this product's repositories commits, "because no use case
/// here spans two aggregates yet - when one does (`20-06`'s provisioning transaction is the likely
/// first), it gets an explicit multi-aggregate port the way AGO Chat's
/// <c>ISiteRegistrationRepository</c> did, rather than a shared unit-of-work nobody can see the
/// boundaries of". This is that use case and this is that port.</para>
///
/// <para><b>Why one transaction rather than three calls.</b> A tenant with no role is a tenant whose
/// operator can do nothing, and a role with no holder is a row nobody can reach; the halfway states
/// are all indistinguishable from a broken shop, and none of them is recoverable by retrying, because
/// the second attempt would collide with the first's public key. One transaction makes the halfway
/// states unrepresentable, which is cheaper than making them recoverable.</para>
/// </summary>
public interface ITenantProvisioningStore
{
    /// <param name="operator">Already holding <paramref name="role"/> - the grant is an in-aggregate
    /// operation (<c>Operator.Grant</c>), so it is part of the object, not a fourth write.</param>
    Task RegisterAsync(Tenant tenant, Role role, Operator @operator, CancellationToken cancellationToken);
}
