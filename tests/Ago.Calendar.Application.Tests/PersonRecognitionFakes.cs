using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`26-268`§2a: the recognition read, faked - the identical shape
/// `PersonBookingsFakes.FakePersonBookingReadStore` already establishes for its sibling handler test.
/// <see cref="AskedFor"/> is what proves the read is tenant-scoped and asks with the phone the caller
/// typed, never a narrowed or normalised one the fake would have to guess at.</summary>
internal sealed class FakePersonRecognitionReadStore(params PersonRecognitionCandidateRow[] rows)
    : IPersonRecognitionReadStore
{
    public List<(TenantId TenantId, string Phone, bool Mask)> AskedFor { get; } = [];

    public Task<IReadOnlyList<PersonRecognitionCandidateRow>> FindByPhoneAsync(
        TenantId tenantId, PhoneNumber phone, bool mask, CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, phone.Value, mask));
        return Task.FromResult<IReadOnlyList<PersonRecognitionCandidateRow>>([.. rows]);
    }
}
