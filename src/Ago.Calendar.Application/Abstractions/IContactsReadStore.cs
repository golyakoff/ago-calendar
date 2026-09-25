using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The tenant contacts report (`20-12`): every <see cref="PersonRecord"/> a tenant has accumulated,
/// in one list. `adr/0184`: what this lists is the calendar's own operational facts about a person -
/// the phone they book with, its two verification marks, the no-show count, when they were first and
/// last seen - keyed by the opaque person id. The person's name and any operator notes are chat's
/// (the account's person registry) and are read from there by the console, display-merged onto these
/// rows; nothing here serves them any more.
///
/// <para><b>A read store, not a repository</b> (adr/0004), the same shape
/// <see cref="IPendingBookingReadStore"/> already established: this returns rows shaped for a screen,
/// never a <see cref="PersonRecord"/> aggregate with its own invariants to enforce. Modelled after
/// `ago-chat`'s <c>OperatorAnalyticsReadStore</c> (`18-08`) as the closest structural precedent - a
/// Dapper read store, tenant-isolated, gated by a permission the handler checks once - adapted for a
/// full personal-data listing rather than an aggregate count.</para>
///
/// <para><b>Every field here is personal data</b> - a phone number and a no-show history keyed by a
/// person are exactly what <see cref="PersonRecord"/>'s own remarks name as the personal data this
/// product still holds. See <c>ago-root/docs/architecture/personal-data.md</c>.</para>
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
/// <para><b>No merge, no duplicate hint (`adr/0184`, author decision O2).</b> `23-60`'s calendar-side
/// merge and its "shares a phone" hint are retired: one person id per person makes duplicates rare, and
/// if a merge is ever wanted again it is a chat-side act on the registry, never a calendar one.</para>
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
/// <see cref="PersonRecord.PhoneVerifiedAt"/>'s own remarks.</param>
/// <param name="PhoneConfirmedByOperatorAt">`23-12`'s own distinct fact - when an operator recorded
/// "I called and it is them", or <see langword="null"/> if nobody has. See
/// <see cref="PersonRecord.PhoneConfirmedByOperatorAt"/>'s own remarks for why this is never merged with
/// <paramref name="PhoneVerifiedAt"/>.</param>
/// <param name="NoShowCount">Read honestly, not fixed here: `20-04`'s own retro note is that nothing
/// in production ever writes this counter up, so it is zero for every person today. `20-12`'s own
/// item file is explicit that fixing the missing writer is a separate item - this report shows the
/// real column, whatever it currently holds, rather than inventing a value to make the screen look
/// more finished than the product is.</param>
/// <param name="PersonId">`adr/0184`: the opaque person id - the key the console uses to fetch the
/// person's name and notes from chat's own Person API and display-merge them onto this row.</param>
public readonly record struct ContactRow(
    Guid PersonId,
    string Phone,
    bool Masked,
    int NoShowCount,
    DateTimeOffset? PhoneVerifiedAt,
    DateTimeOffset? PhoneConfirmedByOperatorAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
