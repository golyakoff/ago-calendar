using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// The chat entry point's first step: resolve the calling site's own tenant and calendar, start a new
/// <see cref="Domain.ChatBookingTask"/>, and offer the services it can book.
///
/// <para><b>`22-04`: the tenant is resolved from <see cref="StartModuleTask.SiteId"/> directly, by
/// id - not through <see cref="EmbedScopeResolver"/>/<see cref="GetBookingSurfaceHandler"/> the way
/// the public widget resolves a tenant from its <see cref="TenantPublicKey"/>.</b> That resolver's
/// preamble exists for an unauthenticated browser caller: resolve by a public, non-secret key, then
/// check the request's <c>Origin</c> against that tenant's allow-list, because CORS is the only
/// boundary a page in someone else's browser can be held to. This call has neither a public key nor a
/// browser - it is server-to-server, and by the time this handler runs
/// <c>ChatModuleTaskEndpoints.HandleStartAsync</c> has already proven, cryptographically, which tenant
/// <see cref="StartModuleTask.SiteId"/> names (<c>IModuleCallCredentialValidator</c>'s own remarks).
/// Routing that already-proven id through the public-key resolver would mean deriving a public key
/// from an id just to immediately resolve the same id back out of it - the "fourth resolution path"
/// this options-class-turned-registry replaces was the *unauthenticated static config* case, not this
/// one; the identical caution does not transfer to a caller whose identity is already proven.</para>
///
/// <para><b>Which calendar answers is derived, not configured.</b> Before this item, a single
/// <c>ChatModuleTaskOptions.CalendarId</c> named the one calendar every chat call acted on. A tenant
/// may publish more than one calendar (<c>IBookingCalendarRepository.ListPublishedAsync</c>'s own
/// remarks), and nothing in this item's own Done-when asks Chat's entry point to offer a choice of
/// calendar before a service - so this handler requires the tenant to have <b>exactly one</b> published
/// calendar and refuses (<see cref="ChatModuleTaskErrors.NotConfigured"/>) otherwise, rather than
/// guessing which of several the visitor meant. A tenant with more than one published calendar wanting
/// to answer chat calls is a real gap, named here rather than solved by picking one arbitrarily - see
/// this item's own report.</para>
/// </summary>
public sealed class StartModuleTaskHandler(
    ITenantRepository tenants,
    IBookingCalendarRepository calendars,
    IBookingSurfaceReadStore surface,
    IChatBookingTaskStore tasks,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ModuleTaskStarted>> HandleAsync(
        StartModuleTask command, CancellationToken cancellationToken)
    {
        // `22-04`: the credential already proved this site id, so a Tenant row keyed by the identical
        // id is the tenant this call is for - see this class's own remarks.
        var tenant = await tenants.GetByIdAsync(new TenantId(command.SiteId), cancellationToken);
        if (tenant is null)
        {
            // No tenant provisioned at this id: this site is not a real, provisioned calendar
            // account, which is exactly "the module is not enabled for this site" - refused, not a
            // deployment fault.
            return ChatModuleTaskErrors.NotConfigured();
        }

        var published = await calendars.ListPublishedAsync(tenant.Id, cancellationToken);
        if (published.Count != 1)
        {
            // Zero: nothing published yet to answer chat with. More than one: which calendar chat
            // should offer is genuinely ambiguous and not this handler's decision to guess - see this
            // class's own remarks.
            return ChatModuleTaskErrors.NotConfigured();
        }

        var calendar = published[0];
        var services = await surface.ListServicesAsync(calendar.Id, cancellationToken);

        var now = clock.UtcNow;
        var task = Domain.ChatBookingTask.Start(
            new ChatBookingTaskId(idGenerator.NewId(now)), tenant.Id, calendar.Id, now);
        await tasks.AddAsync(task, cancellationToken);

        // Empty is a real, legitimate state (GetBookingSurfaceHandler's own remarks: a calendar
        // published with nobody performing anything yet), not special-cased into an error here for
        // the same reason it is not special-cased there.
        var step = ModuleStepFactory.ServiceChoice(services);

        return Result<ModuleTaskStarted>.Success(
            new ModuleTaskStarted(task.Id.Value.ToString(), step, Complete: false));
    }
}
