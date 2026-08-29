using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// The chat entry point's first step: resolve this deployment's statically configured tenant and
/// calendar, start a new <see cref="ChatBookingTask"/>, and offer the services it can book.
///
/// <para><b>Reuses <see cref="GetBookingSurfaceHandler"/> rather than the read store directly.</b>
/// That handler already resolves the tenant by public key and lists every published calendar with its
/// services inlined - exactly the shape this handler needs to both find the configured calendar and
/// read its services in one call, without re-deriving <c>EmbedScopeResolver</c>'s own preamble. The
/// cost is one indexed tenant lookup this handler repeats afterwards for <see cref="TenantId"/> alone
/// (that handler returns only a display name, not the id) - a second primary-key read on a path
/// `adr/0065` itself frames as human-paced, not the hot path <c>BookEventHandler</c>'s own ordering
/// discipline exists for.</para>
///
/// <para><b><c>Origin</c> is always <c>null</c> here.</b> This is a server-to-server call with no
/// browser anywhere in the path, so there is no <c>Origin</c> header to read - the same legitimate
/// "no origin at all" caller <c>OriginPolicy</c> already names `21-01`'s channel adapters as. Note
/// what that leaves genuinely open, stated in this item's own report rather than papered over here:
/// nothing authenticates that a caller hitting this endpoint actually is `Ago.Chat.*`'s server. That
/// gap is real and named, not solved by this parameter.</para>
/// </summary>
public sealed class StartModuleTaskHandler(
    GetBookingSurfaceHandler surfaceHandler,
    ITenantRepository tenants,
    IChatBookingTaskStore tasks,
    ChatModuleTaskOptions options,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ModuleTaskStarted>> HandleAsync(
        StartModuleTask command, CancellationToken cancellationToken)
    {
        var surface = await surfaceHandler.HandleAsync(
            new GetBookingSurface(options.TenantPublicKey, Origin: null), cancellationToken);
        if (!surface.IsSuccess)
        {
            // Whatever PublicBookingErrors said (deliberately vague, written for a stranger on the
            // public internet) is the wrong message here: the caller supplied none of the coordinates
            // that failed to resolve. This deployment's own configuration is what is wrong.
            return ChatModuleTaskErrors.NotConfigured();
        }

        var configuredCalendarId = new CalendarId(options.CalendarId);
        var calendar = surface.Value.Calendars.FirstOrDefault(c => c.CalendarId == configuredCalendarId);
        if (calendar.CalendarId != configuredCalendarId)
        {
            return ChatModuleTaskErrors.NotConfigured();
        }

        var tenant = await tenants.FindByPublicKeyAsync(
            new TenantPublicKey(options.TenantPublicKey), cancellationToken);
        if (tenant is null)
        {
            return ChatModuleTaskErrors.NotConfigured();
        }

        var now = clock.UtcNow;
        var task = Domain.ChatBookingTask.Start(
            new ChatBookingTaskId(idGenerator.NewId(now)), tenant.Id, configuredCalendarId, now);
        await tasks.AddAsync(task, cancellationToken);

        // Empty is a real, legitimate state (GetBookingSurfaceHandler's own remarks: a calendar
        // published with nobody performing anything yet), not special-cased into an error here for
        // the same reason it is not special-cased there.
        var step = ModuleStepFactory.ServiceChoice(calendar.Services);

        return Result<ModuleTaskStarted>.Success(
            new ModuleTaskStarted(task.Id.Value.ToString(), step, Complete: false));
    }
}
