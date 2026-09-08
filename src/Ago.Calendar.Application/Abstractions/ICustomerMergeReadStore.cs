using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-60`/`adr/0147`: the read side of the audit row `ICustomerMergeStore` writes - "the merge is
/// recorded ... and is visible afterwards" (the item's own Done-when, in those words). The same
/// "a dedicated table with its own screen, not a log line" decision <c>IContactPhoneRevealRepository</c>'s
/// own remarks argue for a reveal, restated here because a merge is the same shape of fact: one
/// deliberate, attributable act, not a stream a general-purpose log would blur into everything else
/// an operator did that day.
///
/// <para><b>Raw Npgsql, not EF - the identical "no aggregate, no invariant beyond one row per event"
/// reasoning <c>IContactPhoneRevealRepository</c>'s own remarks give for itself.</b> Nothing about a
/// merge record changes after it is written; there is nothing here for a tracked entity to protect.</para>
/// </summary>
public interface ICustomerMergeReadStore
{
    /// <summary>Keyset by <c>id</c> descending (newest first) - the identical convention
    /// <see cref="IContactPhoneRevealRepository.ListForTenantAsync"/> already uses;
    /// <paramref name="beforeId"/> <see langword="null"/> means the first page.</summary>
    Task<CustomerMergePage> ListForTenantAsync(
        TenantId tenantId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>One merge, read back for a tenant's own audit trail - who did it, when, which record
/// absorbed which, and how many bookings actually moved.</summary>
public sealed record CustomerMergeRecord(
    Guid Id,
    DateTimeOffset MergedAt,
    Guid SurvivorCustomerId,
    Guid AbsorbedCustomerId,
    Guid OperatorId,
    int BookingsMoved);

/// <summary>One keyset page - <c>NextBeforeId</c> <see langword="null"/> once the oldest row has been
/// reached, the identical shape <c>ContactPhoneRevealPage</c> already establishes.</summary>
public sealed record CustomerMergePage(IReadOnlyList<CustomerMergeRecord> Items, Guid? NextBeforeId);
