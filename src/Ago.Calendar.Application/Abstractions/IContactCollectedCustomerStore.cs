using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-59`/`adr/0147`: the calendar's own half of the crossing - "chat publishes; the calendar
/// consumes; neither reads the other's tables." Creates (or, on redelivery, no-ops onto) a
/// <see cref="Customer"/> from a chat-collected phone contact, for a tenant that has this product
/// provisioned.
///
/// <para><b>Not staged for a caller to combine into one save, unlike <c>IRoleAssignmentProjectionStore.StageAsync</c>.</b>
/// The real, load-bearing write is a raw SQL <c>ON CONFLICT</c> upsert - the identical
/// "Postgres arbitrates the conflict inside one statement" reasoning <c>ICustomerRepository</c>'s own
/// remarks give for why the booking path's own find-or-create lives on <c>IBookingStore</c> rather
/// than a tracked EF read-then-write. That statement commits itself, the same "two commits, not one"
/// shape <c>IWorkerQuotaGrantStore</c>'s own remarks describe for the identical reason: an upsert with
/// its own conflict target cannot be expressed as "stage an EF entity" without losing the very
/// arbitration that makes a concurrent redelivery safe.</para>
/// </summary>
public interface IContactCollectedCustomerStore
{
    /// <summary>
    /// Idempotent under redelivery, keyed by <paramref name="sourceContactId"/> - the identical id
    /// <see cref="Customer.SourceContactId"/> stores, so the same chat contact arriving twice (the live
    /// publish and, separately, `ago-chat`'s own retroactive carry-over, or two genuine broker
    /// redeliveries of either) upserts the same row rather than creating a second one.
    /// <paramref name="tenantId"/> is trusted to already have a <c>tenants</c> row - the caller
    /// (<c>ContactCollectedConsumer</c>) checks that first and never calls this method otherwise, the
    /// same "the module is granted" gate the item's own Done-when names.
    /// </summary>
    Task UpsertAsync(
        TenantId tenantId, Guid sourceContactId, PhoneNumber phone, DateTimeOffset recordedAt,
        CancellationToken cancellationToken);
}
