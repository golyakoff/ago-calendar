using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PublicBooking;
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
    // `26-322`: the two read use cases the sole-service skip composes the next step from - the same
    // instances (and DI registrations) ReplyToModuleTaskHandler already depends on, so no wiring change
    // is needed beyond this constructor. Injected rather than reached through `surface` directly because
    // the worker/slot reads must go through EmbedScopeResolver's own tenant/calendar cross-check, which
    // is exactly what these handlers own and a raw read store call would bypass.
    GetBookableWorkersHandler workersHandler,
    GetOpenSlotsHandler slotsHandler,
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

        var step = await BuildFirstStepAsync(task, tenant, services, command.Locale, now, cancellationToken);
        if (!step.IsSuccess)
        {
            return step.Error!.Value;
        }

        // `26-322`: persisted once, after any auto-skip has advanced the task - AddAsync now carries the
        // real starting state (which for a one-service calendar is already AwaitingWorkerChoice, and for
        // a one-service-one-worker calendar already AwaitingDateChoice), never the bare
        // AwaitingServiceChoice a solo tenant would in fact never wait in. Moved below the reads it used
        // to sit above because the worker/slot reads go through EmbedScopeResolver, not this task store,
        // so nothing here depends on the row being persisted first.
        await tasks.AddAsync(task, cancellationToken);

        return Result<ModuleTaskStarted>.Success(
            new ModuleTaskStarted(task.Id.Value.ToString(), step.Value, Complete: false));
    }

    private async Task<Result<ModuleStep>> BuildFirstStepAsync(
        Domain.ChatBookingTask task, Tenant tenant, IReadOnlyList<BookableServiceRow> services, string locale,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // `26-322`: more than one selectable service (or none) - offer the service choice unchanged.
        // Empty is a real, legitimate state (GetBookingSurfaceHandler's own remarks: a calendar published
        // with nobody performing anything yet), not special-cased into an error here for the same reason
        // it is not special-cased there.
        // `25-37`: the site's own configured widget language, handed straight through - see
        // ModuleStepFactory's own remarks on why this is never stored on the new ChatBookingTask itself.
        if (services.Count != 1)
        {
            return Result<ModuleStep>.Success(ModuleStepFactory.ServiceChoice(services, locale));
        }

        // `26-322`: exactly one selectable service - auto-select it (the visitor is never asked to
        // "choose" from a list of one) and compose the worker step, which itself skips straight to the
        // date round when exactly one worker is eligible. So a one-service-one-worker calendar opens
        // directly on the date round, both steps vanished; the confirmation card still names the service
        // and the worker, so nothing the visitor picked is hidden from them.
        task.AutoChooseService(services[0].ServiceId, now);
        return await ChatBookingStepComposer.WorkerStepAsync(
            task, tenant.PublicKey.Value, locale, now, workersHandler, slotsHandler, cancellationToken);
    }
}
