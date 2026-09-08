using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`23-60`/`adr/0147`: hand-written fakes for the merge use cases, the same "a field holding
/// the calls" discipline every other fake in this test project uses.</summary>
internal sealed class FakeCustomerRepositoryForMerge(params Customer[] customers) : ICustomerRepository
{
    private readonly Dictionary<CustomerId, Customer> _byId = customers.ToDictionary(c => c.Id);

    /// <summary>The assertion surface for "a refused caller never even looked the customer up" -
    /// <see cref="BookingLifecycleFakes"/>'s own <c>Loaded</c> field states the identical
    /// reasoning.</summary>
    public List<CustomerId> Loaded { get; } = [];

    public Task<Customer?> FindByPhoneAsync(TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the merge use cases.");

    public Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken)
    {
        Loaded.Add(id);
        return Task.FromResult(_byId.GetValueOrDefault(id));
    }

    public Task AddAsync(Customer customer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the merge use cases.");

    public Task SaveAsync(Customer customer, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "MergeCustomersHandler never saves a Customer directly - persistence goes through " +
            "ICustomerMergeStore, which is what makes the booking reassignment and both aggregates' " +
            "own new state one transaction. Reaching this would be a load-mutate-save regression.");
}

/// <summary>Records every call in full - which two aggregates it was handed, in exactly the mutated
/// state the handler produced, so a test can assert on <see cref="Customer.NoShowCount"/>,
/// <see cref="Customer.MergedIntoCustomerId"/> and friends without a second round trip through a real
/// store.</summary>
internal sealed class FakeCustomerMergeStore : ICustomerMergeStore
{
    public List<MergeCall> Calls { get; } = [];

    public int BookingsMovedToReturn { get; set; }

    public Task<CustomerMergeResult> MergeAsync(
        TenantId tenantId,
        Customer survivor,
        Customer absorbed,
        OperatorId operatorId,
        Guid mergeId,
        DateTimeOffset mergedAt,
        CancellationToken cancellationToken)
    {
        Calls.Add(new MergeCall(tenantId, survivor, absorbed, operatorId, mergeId, mergedAt));
        return Task.FromResult(new CustomerMergeResult(BookingsMovedToReturn));
    }

    internal sealed record MergeCall(
        TenantId TenantId, Customer Survivor, Customer Absorbed, OperatorId OperatorId, Guid MergeId, DateTimeOffset MergedAt);
}

internal sealed class FakeCustomerMergePreviewReadStore(IReadOnlyList<CustomerMergePreviewBookingRow>? rows = null)
    : ICustomerMergePreviewReadStore
{
    public List<(TenantId TenantId, CustomerId First, CustomerId Second)> AskedFor { get; } = [];

    public Task<IReadOnlyList<CustomerMergePreviewBookingRow>> ListForCandidatesAsync(
        TenantId tenantId, CustomerId firstCustomerId, CustomerId secondCustomerId, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, firstCustomerId, secondCustomerId));
        return Task.FromResult(rows ?? []);
    }
}

internal sealed class FakeCustomerMergeReadStore(params CustomerMergeRecord[] records) : ICustomerMergeReadStore
{
    public List<(TenantId TenantId, Guid? BeforeId, int Limit)> AskedFor { get; } = [];

    public Task<CustomerMergePage> ListForTenantAsync(
        TenantId tenantId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, beforeId, limit));
        return Task.FromResult(new CustomerMergePage([.. records], null));
    }
}
