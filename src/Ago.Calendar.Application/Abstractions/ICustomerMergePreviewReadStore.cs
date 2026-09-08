using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-60`/`adr/0147`: "seeing both sets of bookings before deciding" - the item's own words for what
/// the confirmation step must show, and the read this port answers. Not a reuse of
/// <see cref="IConfirmedBookingReadStore"/>: that store answers "what is on, tenant-wide, in a date
/// range" and has no notion of a single customer's whole history, while this screen's whole point is
/// one customer's history in full - every status, not only <see cref="EventStatus.Booked"/>, because a
/// cancellation or a no-show under one identity is exactly the fact an operator needs before deciding
/// the two identities are the same person.
///
/// <para><b>One call for both customers, not two - and that shape is the structural guarantee, not a
/// convenience.</b> <see cref="ListForCandidatesAsync"/> takes <paramref name="tenantId"/> once,
/// alongside both customer ids, so the underlying query resolves both against the same tenant-scoped
/// <c>WHERE</c> clause in a single statement - the identical "the read cannot even be asked to cross a
/// tenant" shape <c>ICustomerMergeStore</c>'s own remarks describe for the write side. A caller who
/// wanted a cross-tenant preview would have no method to call that could express it.</para>
/// </summary>
public interface ICustomerMergePreviewReadStore
{
    Task<IReadOnlyList<CustomerMergePreviewBookingRow>> ListForCandidatesAsync(
        TenantId tenantId, CustomerId firstCustomerId, CustomerId secondCustomerId, CancellationToken cancellationToken);
}

/// <summary>One booking, on one of the two candidate cards - every status, oldest first, so the
/// console can render each customer's own history as a timeline rather than a bag the caller has to
/// sort.</summary>
public readonly record struct CustomerMergePreviewBookingRow(
    CustomerId CustomerId,
    EventId BookingId,
    EventStatus Status,
    string? ServiceName,
    string WorkerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate);
