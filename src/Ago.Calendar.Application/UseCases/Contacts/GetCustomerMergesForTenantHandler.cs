using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-60`/`adr/0147`'s own Done-when: "the merge is recorded with who and when, and is visible
/// afterwards." This *is* that visibility - the identical audit-view shape
/// <see cref="GetPhoneRevealsForTenantHandler"/> already establishes, restated here for a second,
/// independent kind of consequential act.
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/>, the same permission
/// <see cref="GetPhoneRevealsForTenantHandler"/> uses and for the identical reason</b> - every
/// operator who can merge a lead card is not thereby entitled to the tenant's whole merge history
/// (every customer's, not just their own), which is a materially wider read.</para>
/// </summary>
public sealed class GetCustomerMergesForTenantHandler(ICustomerMergeReadStore merges, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<CustomerMergePage>> HandleAsync(
        GetCustomerMergesForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CalendarConfigure);
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await merges.ListForTenantAsync(query.TenantId, query.Before, limit, cancellationToken);
        return page;
    }
}
