using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The tenant contacts report (`20-12`): every <see cref="Customer"/> lead card a tenant has
/// accumulated, in one list.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the same shape
/// <see cref="IPendingBookingReadStore"/> already established: this returns rows shaped for a screen,
/// never a <see cref="Customer"/> aggregate with its own invariants to enforce. Modelled after
/// `ago-chat`'s <c>OperatorAnalyticsReadStore</c> (`18-08`) as the closest structural precedent - a
/// Dapper read store, tenant-isolated, gated by a permission the handler checks once - adapted for a
/// full personal-data listing rather than an aggregate count.</para>
///
/// <para><b>Every field here is personal data</b> - phone, display name, notes, and the no-show count
/// are exactly the three fields <see cref="Customer"/>'s own remarks name as "the only entity in this
/// product that describes a natural person". See <c>ago-root/docs/architecture/personal-data.md</c>'s
/// own `20-12` subsection for what widens as a result of this read store existing.</para>
/// </summary>
public interface IContactsReadStore
{
    Task<IReadOnlyList<ContactRow>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <param name="NoShowCount">Read honestly, not fixed here: `20-04`'s own retro note is that nothing
/// in production ever writes this counter up, so it is zero for every customer today. `20-12`'s own
/// item file is explicit that fixing the missing writer is a separate item - this report shows the
/// real column, whatever it currently holds, rather than inventing a value to make the screen look
/// more finished than the product is.</param>
public readonly record struct ContactRow(
    CustomerId CustomerId,
    PhoneNumber Phone,
    string? DisplayName,
    string? Notes,
    int NoShowCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
