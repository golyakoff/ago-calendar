using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`22-05`: moved out of the deleted `AccessControlFakes.cs` - this fake belongs to
/// <c>ContactsHandlerTests</c> (<c>GetTenantContactsHandler</c>), which has nothing to do with access
/// control and never did; it only ever shared a file with the fakes that did.</summary>
internal sealed class FakeContactsReadStore(params ContactRow[] rows) : IContactsReadStore
{
    public List<(TenantId TenantId, bool Mask)> AskedFor { get; } = [];

    public Task<IReadOnlyList<ContactRow>> ListForTenantAsync(
        TenantId tenantId, bool mask, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, mask));
        return Task.FromResult<IReadOnlyList<ContactRow>>([.. rows]);
    }
}
