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
/// </summary>
public sealed class GetTenantContactsHandler(IContactsReadStore contacts, IPermissionChecker permissions)
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

        var rows = await contacts.ListForTenantAsync(query.TenantId, cancellationToken);
        return Result<IReadOnlyList<ContactRow>>.Success(rows);
    }
}
