using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>The same shape `ContactsFakes.cs`'s own `FakeContactsReadStore` establishes for its
/// sibling handler test, restated for `23-34`'s own read store.</summary>
internal sealed class FakeConfirmedBookingReadStore(params ConfirmedBookingRow[] rows) : IConfirmedBookingReadStore
{
    public List<(TenantId TenantId, DateOnly From, DateOnly To, bool Mask)> AskedFor { get; } = [];

    public Task<IReadOnlyList<ConfirmedBookingRow>> GetConfirmedForTenantAsync(
        TenantId tenantId, DateOnly from, DateOnly to, bool mask, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, from, to, mask));
        return Task.FromResult<IReadOnlyList<ConfirmedBookingRow>>([.. rows]);
    }
}
