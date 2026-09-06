using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-12`/`decisions.md` §5: the write side of "a reveal is an act that leaves a record" - one row
/// per deliberate unmasking of a customer's phone number.
///
/// <para><b>A dedicated table, not a widened <see cref="Domain.EventStatus"/>-style closed
/// vocabulary or a repurposed audit log - the decision this item's own brief asked to be made and
/// argued, not defaulted.</b> This product has no <c>access_records</c>-shaped table at all (`24-12`
/// is `ago-chat`'s own, and never crosses the product boundary), so there is no existing sink to
/// widen and the question `adr/0123` answered for the account side - "does a reveal belong beside an
/// existing audit table, or does it get one of its own" - reduces here to "build one", for the
/// identical reasoning that ADR gives: a reveal is not a read that crosses a tenant or conversation
/// boundary an operator would not otherwise cross (the revealing operator already holds
/// <see cref="Permission.CustomerRead"/>, the same permission that already lets them read every other
/// unmasked field the list responses carry), it is one specific field this tenant's own setting chose
/// to mask, shown on purpose, for attribution. §5's own counter-instruction - reveal counts belong in
/// an audit view, never in the report a person is judged on - argues for exactly this: a table with
/// its own screen and its own caveat text, not a stream folded into a general-purpose log.</para>
///
/// <para><b>Raw Npgsql, not EF - the identical "no aggregate, no invariant beyond one row per event"
/// reasoning `adr/0123`'s own remarks give for `ago-chat`'s <c>contact_reveals</c>.</b> Written by
/// <c>RevealCustomerPhoneHandler</c> (<c>Ago.Calendar.Application</c>) - the acting operator's id is
/// already a field on its own command, so there is no second write site.</para>
/// </summary>
public interface IContactPhoneRevealRepository
{
    Task RecordAsync(ContactPhoneRevealToWrite reveal, CancellationToken cancellationToken);

    /// <summary>The tenant's own read-back - `23-12`'s own Done-when: "the tenant can read the reveal
    /// record, and the screen states what it is for." Keyset by <c>id</c> descending (newest first),
    /// the same convention `ago-chat`'s own <c>IContactRevealRepository.ListForSiteAsync</c> already
    /// uses; <paramref name="beforeId"/> <see langword="null"/> means the first page.</summary>
    Task<ContactPhoneRevealPage> ListForTenantAsync(
        TenantId tenantId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>One reveal to be recorded - who revealed what, when, and which surface asked (`23-12`'s
/// own Scope, in those words). Deliberately carries no <see cref="PhoneNumber"/> - the same "record
/// that a read happened, not what was returned" discipline `ago-chat`'s own
/// <c>ContactRevealToWrite</c> already states for itself.</summary>
public sealed record ContactPhoneRevealToWrite(
    Guid Id,
    DateTimeOffset OccurredAt,
    TenantId TenantId,
    CustomerId CustomerId,
    OperatorId OperatorId,
    string Surface);

/// <summary>One row, read back for a tenant's own report - the same fields
/// <see cref="ContactPhoneRevealToWrite"/> wrote, nothing more (in particular, never the phone
/// number itself).</summary>
public sealed record ContactPhoneRevealItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid CustomerId,
    Guid OperatorId,
    string Surface);

/// <summary>One keyset page of <see cref="IContactPhoneRevealRepository.ListForTenantAsync"/> -
/// <c>NextBeforeId</c> <see langword="null"/> once the oldest row has been reached.</summary>
public sealed record ContactPhoneRevealPage(IReadOnlyList<ContactPhoneRevealItem> Items, Guid? NextBeforeId);
