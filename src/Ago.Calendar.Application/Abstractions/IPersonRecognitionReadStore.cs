using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `26-268`§2a/`adr/0188`: the phone-based client recognition read - "who has already booked with this
/// number, in this tenant" - the read that lets the manual-entry dialog offer «Это он» / a pick-list /
/// «Новый клиент» instead of always minting a fresh person (`EnterManualBookingHandler`'s own
/// unconditional-mint, which this makes conditional).
///
/// <para><b>Surfaces candidates; never resolves one.</b> This is the whole reason `adr/0188` states this
/// does not reopen `adr/0147` ("a phone is a hint, not proof"): the method below returns every
/// <see cref="PersonRecord"/> a tenant holds for a number, in a plain list, with no attempt to pick "the"
/// one - a design that made a single choice for the caller would be exactly the identity assertion
/// `adr/0147` refuses. The **operator** is the one who turns a candidate into a decision, by choosing a
/// row (or «Новый клиент», not represented here at all - it is simply "call
/// <c>EnterManualBookingHandler</c> with no reuse id", the unconditional-mint path already built).</para>
///
/// <para><b>A read store, not a repository</b> (adr/0004), the identical shape
/// <see cref="IContactsReadStore"/> and <see cref="IPersonBookingReadStore"/> already establish: rows
/// shaped for a screen, never a <see cref="PersonRecord"/> aggregate loaded to answer a display
/// question. Kept off <see cref="IPersonRecordRepository"/> deliberately - see that port's own remarks
/// on why a phone-to-person lookup does not belong beside its load-mutate-save methods even now that one
/// exists in this product.</para>
///
/// <para><b>Tenant-scoped, always.</b> `adr/0147`/`adr/0184`'s existing discipline: a phone match at
/// another shop is not this shop's fact, the identical predicate
/// <see cref="IPersonRecordRepository.FindPhoneVerifiedAtAsync"/> already applies for the same
/// reason.</para>
/// </summary>
public interface IPersonRecognitionReadStore
{
    /// <param name="mask">`23-12`'s own gate, resolved once by the handler against the tenant's rung and
    /// handed down - the identical split every other read store in this product already uses. A
    /// recognition candidate still carries a phone (the operator wants to see what they are matching
    /// against), so it is gated exactly like every other screen that shows one.</param>
    Task<IReadOnlyList<PersonRecognitionCandidateRow>> FindByPhoneAsync(
        TenantId tenantId, PhoneNumber phone, bool mask, CancellationToken cancellationToken);
}

/// <summary>
/// One existing client who has booked with this number before, for the operator to look at and either
/// accept («Это он», carrying <see cref="PersonId"/> back as <c>EnterManualBooking.ReusePersonId</c>) or
/// pass over («Новый клиент»). Deliberately carries no display name - `adr/0184` keeps that in chat, and
/// the console/Android client-side display-merge (<see cref="ContactRow"/>'s own established pattern)
/// is what turns <see cref="PersonId"/> into the name and any returning-client copy the dialog shows.
/// </summary>
/// <param name="PersonId">The id to hand back as <c>EnterManualBooking.ReusePersonId</c> if the operator
/// picks this candidate.</param>
/// <param name="Phone">The already-formatted display value - masked or real, the identical convention
/// <see cref="ContactRow.Phone"/> already establishes. Present mainly so a masked-rung tenant's operator
/// can still see *which* number they are matching, even though every candidate here matched the same
/// number the operator just typed.</param>
/// <param name="Masked">Whether <see cref="Phone"/> above is the rung-masked display value.</param>
/// <param name="NoShowCount">`20-04`'s own fact, carried the same way <see cref="ContactRow.NoShowCount"/>
/// already does - read honestly, whatever the column currently holds.</param>
/// <param name="BookingCount">How many bookings (<see cref="EventStatus.PendingConfirmation"/>,
/// <see cref="EventStatus.Booked"/> or <see cref="EventStatus.NoShow"/> - <see cref="IPersonBookingReadStore"/>'s
/// own held-status set) this person has ever made in this tenant - the cheap "returning client" hint
/// `docs/backlog/26-268-manual-booking-entry.md` §3.4 asks for («Постоянный клиент · 3 записи»), computed
/// in the same query rather than a second round trip to <see cref="IPersonBookingReadStore"/> (which
/// would also hand back every booking's own detail this screen has no use for).</param>
/// <param name="PhoneVerifiedAt">Carried for completeness - the same fact <see cref="ContactRow.PhoneVerifiedAt"/>
/// already exposes.</param>
/// <param name="PhoneConfirmedByOperatorAt">Carried for completeness - the same fact
/// <see cref="ContactRow.PhoneConfirmedByOperatorAt"/> already exposes.</param>
/// <param name="FirstSeenAt">When this person's record was first created in this tenant.</param>
/// <param name="LastSeenAt">This person's own most recent activity - also this store's own ordering key
/// (newest first), the identical choice <see cref="IContactsReadStore"/> already makes.</param>
public readonly record struct PersonRecognitionCandidateRow(
    Guid PersonId,
    string Phone,
    bool Masked,
    int NoShowCount,
    int BookingCount,
    DateTimeOffset? PhoneVerifiedAt,
    DateTimeOffset? PhoneConfirmedByOperatorAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
