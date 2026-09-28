using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PersonBookings;

public readonly record struct GetPersonBookings(OperatorId OperatorId, TenantId TenantId, Guid PersonId);

/// <summary>
/// `26-269`: the read the client-detail hub needs and the tenant-wide
/// <see cref="Ago.Calendar.Application.UseCases.ConfirmedBookings.GetConfirmedBookingsForTenantHandler"/>
/// cannot answer - "this person's bookings, past and upcoming together" rather than "everyone's
/// bookings in a date window". A sibling of that handler in the exact shape
/// `docs/backlog/26-269-clients-redesign.md` §5 calls for: same permission, same rung-masking split,
/// filtered by person instead of by date.
///
/// <para><b>Gated on <see cref="Permission.CustomerRead"/> alone - the identical permission and the
/// identical reasoning <see cref="Ago.Calendar.Application.UseCases.ConfirmedBookings.GetConfirmedBookingsForTenantHandler"/>'s
/// own doc comment gives for itself.</b> Both screens answer "may this operator see a customer's
/// personal data" - a client's own booking history is exactly that.</para>
///
/// <para><b>No range to validate.</b> Unlike its tenant-wide sibling, this query carries no
/// <c>from</c>/<c>to</c> - the client-detail hub wants the whole held history at once, so there is no
/// "well-formed range" question this handler needs to ask before going to the store.</para>
///
/// <para><b>Never an error for "this person has no bookings".</b> A person id belonging to another
/// tenant, or one that never booked at all, is indistinguishable from "no history yet" at this read's
/// own layer - the store's own tenant-and-person predicate (<see cref="IPersonBookingReadStore"/>'s own
/// remarks) already makes both come back as an honest empty list, which is exactly what the design
/// calls for: an empty result, never a refusal, for a client the operator is legitimately looking at
/// who simply has not booked yet.</para>
/// </summary>
public sealed class GetPersonBookingsHandler(
    IPersonBookingReadStore bookings,
    IPermissionChecker permissions,
    IContactVisibilityProjectionStore visibility)
{
    public async Task<Result<IReadOnlyList<PersonBookingRow>>> HandleAsync(
        GetPersonBookings query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);
        if (!allowed)
        {
            return PersonBookingsErrors.Forbidden(Permission.CustomerRead);
        }

        // `23-12`: resolved only once the permission gate above has already passed - the identical
        // ordering `GetConfirmedBookingsForTenantHandler`'s own remarks give for itself.
        var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
        var mask = rung == ContactVisibility.MaskedWithReveal;

        var rows = await bookings.GetForPersonAsync(query.TenantId, query.PersonId, mask, cancellationToken);
        return Result<IReadOnlyList<PersonBookingRow>>.Success(rows);
    }
}
