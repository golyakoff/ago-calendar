using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Application.UseCases.Tenancies;

/// <summary>
/// `22-14`/`adr/0100`: backs <c>GET /api/v1/me/tenancies</c> - the enumeration the tenant switcher
/// needs, and the one operator-reachable read in this product that does not take a
/// <see cref="Ago.Calendar.Domain.TenantId"/>.
///
/// <para><b>No <see cref="IPermissionChecker"/> call, and that is not an omission.</b> The three
/// things a permission check would defend are all absent here: there is no caller-supplied tenant to
/// scope to, no other tenant's data in the answer, and no principal-with-a-tenant to check against -
/// an identity holding calendar grants on two accounts fails <c>CalendarClaims.OperatorPolicy</c> by
/// construction until it names one, which is precisely the state this read exists to get somebody out
/// of. What replaces the check is narrower than one: the query filters on an
/// <see cref="Ago.Calendar.Domain.OperatorId"/> derived from the validated token's own <c>sub</c>, so
/// the row set this handler can ever see is already restricted to that identity's own projection rows
/// before a <c>tenants</c> row is joined in. Structurally the same category, and the same argument,
/// as `ago-chat`'s own <c>ListMyTenanciesHandler</c> (`13-07`/`adr/0068`).</para>
///
/// <para><b>Returns the rows, not a <c>Result</c>.</b> There is no refusal this handler can produce -
/// an identity with no tenancies gets an empty list, which is a true answer rather than an error, and
/// the console renders it the same way it renders "no calendar here" today.</para>
/// </summary>
public sealed class ListMyTenanciesHandler(ITenancyReadStore tenancies)
{
    public Task<IReadOnlyList<TenancyRow>> HandleAsync(
        ListMyTenancies query, CancellationToken cancellationToken) =>
        tenancies.ListForOperatorAsync(query.OperatorId, cancellationToken);
}
