using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>The same shape `ConfirmedBookingsFakes.cs`'s own `FakeConfirmedBookingReadStore` establishes
/// for its sibling handler test, restated for `26-269`'s own read store.</summary>
internal sealed class FakePersonBookingReadStore(params PersonBookingRow[] rows) : IPersonBookingReadStore
{
    public List<(TenantId TenantId, Guid PersonId, bool Mask)> AskedFor { get; } = [];

    public Task<IReadOnlyList<PersonBookingRow>> GetForPersonAsync(
        TenantId tenantId, Guid personId, bool mask, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, personId, mask));
        return Task.FromResult<IReadOnlyList<PersonBookingRow>>([.. rows]);
    }
}
