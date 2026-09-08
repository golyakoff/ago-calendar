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
///
/// <para><b>`23-12`: <paramref name="mask"/> is decided once by <c>GetTenantContactsHandler</c>
/// (which reads the tenant's rung through <see cref="IContactVisibilityProjectionStore"/>) and handed
/// down rather than re-checked here</b> - the same "the store answers the permission question it is
/// asked, not one it goes and looks up itself" split <see cref="IPendingBookingReadStore"/>'s own
/// <c>includeContactData</c> parameter already draws. When <see langword="true"/>, every
/// <see cref="ContactRow.Phone"/> this store returns is already the masked string; the real value
/// never crosses into the row at all, so there is no flag a careless caller could ignore and
/// accidentally forward the unmasked number.</para>
///
/// <para><b>`23-60`/`adr/0147`: excludes a row once <see cref="Customer.MergedIntoCustomerId"/> is
/// set.</b> A tombstoned row is not a lead card any operator should still be acting on - it exists so
/// <see cref="Event.CustomerId"/>'s own foreign key and <c>ICustomerMergeReadStore</c>'s own audit
/// trail still resolve, not so it keeps appearing on the ordinary contacts screen a merge was
/// performed specifically to clean up. Each surviving row's own
/// <see cref="ContactRow.DuplicatePhoneCustomerIds"/> is this store's other new fact - "share a phone
/// or an e-mail" is the item's own Scope line; this product's <see cref="Customer"/> has no e-mail
/// field to compare (<c>docs/adr/0161-*</c> states this as a scope boundary rather than a gap this
/// item silently leaves), so the comparison is phone alone.</para>
/// </summary>
public interface IContactsReadStore
{
    Task<IReadOnlyList<ContactRow>> ListForTenantAsync(
        TenantId tenantId, bool mask, CancellationToken cancellationToken);
}

/// <param name="Phone">The already-formatted display value - masked (<see cref="PhoneNumber.Masked"/>)
/// when the caller asked for <c>mask: true</c>, the plain <see cref="PhoneNumber.Value"/> otherwise.
/// A <see cref="string"/>, not a <see cref="PhoneNumber"/>: a masked value (bullets in the middle)
/// cannot satisfy that type's own E.164 validation, and this row is a screen shape, not a domain
/// value - the same "a read model holds what a screen prints, not a re-parseable domain type"
/// reasoning <see cref="IPendingBookingReadStore"/>'s own remarks already give for the ids it does
/// keep strongly typed versus the ones it does not.</param>
/// <param name="Masked">Whether <see cref="Phone"/> above is the masked form. Carried explicitly
/// rather than left for a caller to infer from the string's own shape - the same reasoning `ago-chat`'s
/// own <c>VisitorContactDetailDto.Masked</c> field gives for itself.</param>
/// <param name="PhoneVerifiedAt">`20-09`'s own fact, exposed here for the first time: when this
/// number was proven reachable by SMS code, or <see langword="null"/> if it never has been. See
/// <see cref="Customer.PhoneVerifiedAt"/>'s own remarks.</param>
/// <param name="PhoneConfirmedByOperatorAt">`23-12`'s own distinct fact - when an operator recorded
/// "I called and it is them", or <see langword="null"/> if nobody has. See
/// <see cref="Customer.PhoneConfirmedByOperatorAt"/>'s own remarks for why this is never merged with
/// <paramref name="PhoneVerifiedAt"/>.</param>
/// <param name="NoShowCount">Read honestly, not fixed here: `20-04`'s own retro note is that nothing
/// in production ever writes this counter up, so it is zero for every customer today. `20-12`'s own
/// item file is explicit that fixing the missing writer is a separate item - this report shows the
/// real column, whatever it currently holds, rather than inventing a value to make the screen look
/// more finished than the product is.</param>
/// <param name="DuplicatePhoneCustomerIds">`23-60`/`adr/0147`: every other live (not tombstoned)
/// customer in this tenant whose <see cref="Customer.Phone"/> equals this row's own - what the
/// console's "shares a contact detail" hint and merge action are built from. Empty for the ordinary
/// case (no known duplicate). Computed by the store from the same rows it already read for this
/// tenant, never a second query - grouping a list the handler already has in hand costs nothing a
/// fresh round trip would.</param>
public readonly record struct ContactRow(
    CustomerId CustomerId,
    string Phone,
    bool Masked,
    string? DisplayName,
    string? Notes,
    int NoShowCount,
    DateTimeOffset? PhoneVerifiedAt,
    DateTimeOffset? PhoneConfirmedByOperatorAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<CustomerId> DuplicatePhoneCustomerIds);
