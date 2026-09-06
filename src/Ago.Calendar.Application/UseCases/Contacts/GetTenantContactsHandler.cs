using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// The tenant contacts report `20-12` scoped as this item's own new kind of screen: a full personal-
/// data listing, not an aggregate count the way `18-08`'s analytics report is. Gated on
/// <see cref="Permission.CustomerRead"/> - the same permission the queue's own phone field checks,
/// deliberately: both surfaces answer "may this operator see a customer's personal data", and a tenant
/// that grants one and not the other is expressing a real, single decision about who can see contact
/// information, not two unrelated ones.
///
/// <para><b>`23-12`: a second, independent read - the tenant's own rung - resolved only once the
/// permission gate above has already passed.</b> The same ordering
/// <c>GetPendingBookingsForTenantHandler</c>'s own remarks give for its identical second permission
/// check: there is no reason to ask what rung a tenant is on for a caller who is about to be refused
/// the whole list anyway.</para>
/// </summary>
public sealed class GetTenantContactsHandler(
    IContactsReadStore contacts, IPermissionChecker permissions, IContactVisibilityProjectionStore visibility)
{
    public async Task<Result<IReadOnlyList<ContactRow>>> HandleAsync(
        GetTenantContacts query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CustomerRead);
        }

        var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
        var mask = rung == ContactVisibility.MaskedWithReveal;

        var rows = await contacts.ListForTenantAsync(query.TenantId, mask, cancellationToken);
        return Result<IReadOnlyList<ContactRow>>.Success(rows);
    }
}
