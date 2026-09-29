using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PersonRecognition;

/// <summary>
/// `26-268`§2a/`adr/0188`: the manual-entry dialog's own first step - before the operator types a name,
/// look up whether this number already belongs to a client of this tenant, so the dialog can offer
/// «Это он» / a pick-list / «Новый клиент» instead of always minting fresh (`EnterManualBookingHandler`'s
/// own §3.4 default, which this read makes conditional rather than replacing).
///
/// <para><b>Gated on <see cref="Permission.CustomerRead"/> alone - the identical permission and
/// reasoning <see cref="Ago.Calendar.Application.UseCases.Contacts.GetTenantContactsHandler"/>'s own doc
/// comment gives for itself.</b> A phone-matched candidate list is exactly the personal-data question
/// that permission already answers: "may this operator see a customer's own contact/booking facts". No
/// new permission - confirmed against `docs/backlog/26-268-manual-booking-entry.md` §2a's own
/// instruction not to invent one, and there is no separate "who may reuse a client" trust distinct from
/// "who may see one" in this product's v1 role model.</para>
///
/// <para><b>Never a merge, never a single answer - `adr/0188`'s own point.</b> This handler does not
/// decide anything; it is a straight pass-through to <see cref="IPersonRecognitionReadStore"/>, which may
/// legitimately answer with zero, one, or several rows for the same number (`adr/0147`: a phone can be
/// shared). The operator's own choice, made in <c>EnterManualBookingHandler</c> by supplying
/// <c>ReusePersonId</c> or not, is the human confirmation that keeps this read from reopening the
/// auto-merge question `adr/0147` already closed.</para>
///
/// <para><b>Phone validated here, permission checked first.</b> The identical ordering
/// <c>EnterManualBookingHandler</c> already uses: a caller with no right never learns whether the phone
/// they typed even parses, let alone whether it matches anybody.</para>
/// </summary>
public sealed class GetPersonCandidatesByPhoneHandler(
    IPersonRecognitionReadStore candidates,
    IPermissionChecker permissions,
    IContactVisibilityProjectionStore visibility)
{
    public async Task<Result<IReadOnlyList<PersonRecognitionCandidateRow>>> HandleAsync(
        GetPersonCandidatesByPhone query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);
        if (!allowed)
        {
            return PersonRecognitionErrors.Forbidden(Permission.CustomerRead);
        }

        PhoneNumber phone;
        try
        {
            phone = new PhoneNumber(query.Phone);
        }
        catch (ArgumentException exception)
        {
            return PersonRecognitionErrors.InvalidPhone(exception.Message);
        }

        // `23-12`: resolved only once the permission gate above has already passed - the identical
        // ordering every other read store's own handler in this product uses.
        var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
        var mask = rung == ContactVisibility.MaskedWithReveal;

        var rows = await candidates.FindByPhoneAsync(query.TenantId, phone, mask, cancellationToken);
        return Result<IReadOnlyList<PersonRecognitionCandidateRow>>.Success(rows);
    }
}
