using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-12`'s own Done-when: "the tenant can read the reveal record." This *is* the audit view
/// `decisions.md` §5's amendment asks for - a separate, deliberately unaggregated list of individual
/// reveals, never a per-operator count a staff-comparison screen could quote. Nothing in this product
/// reads this table to build one, and this item adds no such reader.
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/>, not <see cref="Permission.CustomerRead"/> -
/// the identical reasoning `ago-chat`'s own <c>GetContactRevealsForSiteHandler</c> gives for its own
/// sibling audit read.</b> Every operator who can reveal a customer's phone is not thereby trusted to
/// see the whole tenant's own reveal history, which is a materially wider read (every customer's
/// reveals, not just the ones this operator triggered). <c>CalendarConfigure</c> already gates this
/// product's one other admin-facing report class (`GetWorkerSlotsHandler`'s configuration
/// screen).</para>
/// </summary>
public sealed class GetPhoneRevealsForTenantHandler(
    IContactPhoneRevealRepository reveals, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<ContactPhoneRevealPage>> HandleAsync(
        GetPhoneRevealsForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CalendarConfigure);
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await reveals.ListForTenantAsync(query.TenantId, query.Before, limit, cancellationToken);
        return page;
    }
}
