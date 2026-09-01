using Ago.Calendar.Application.UseCases.AccessControl;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.ChatModuleTask;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Application.UseCases.Cors;
using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Application.UseCases.WorkerSlots;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Redis;
using Ago.Calendar.Infrastructure.Time;
using Ago.Platform.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Module;

/// <summary>
/// The one <see cref="IProductModule"/> every AGO Calendar host loads
/// (docs/architecture/clean-architecture.md), and the second implementation of that interface in
/// existence - which is the point of `20-00`: the platform's hosting seam had exactly one consumer
/// until now, and an abstraction with one caller is a guess about the second.
///
/// <para>`20-02` is the item that gave it something to register. Both adapters and all three
/// handlers are wired here rather than in each host's <c>Program.cs</c>, because the composition is
/// identical for the API and the Worker - the hosts differ in what they *run*, never in what the
/// product is (adr/0013). What stays in a host is only what is genuinely host-shaped: the
/// materialisation job's own options and its <c>AddHostedService</c> registration, which belong to
/// the one host that runs it.</para>
/// </summary>
public sealed class CalendarModule : IProductModule
{
    public string Name => "Ago.Calendar";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Configuration first, the environment variable as the fallback. `20-02` read only the
        // variable; `20-03` widened it because a test host has to be able to point this module at a
        // Testcontainers Postgres without mutating process-wide state that every other test in the
        // assembly shares. Nothing is weakened by the widening: what must never happen is a
        // credential committed to a settings file, and reading *configuration* does not commit
        // anything - a deployment still supplies the value through the environment.
        var connectionString =
            configuration.GetConnectionString("Calendar")
            ?? Environment.GetEnvironmentVariable("AGO_CALENDAR_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings:Calendar or AGO_CALENDAR_CONNECTION_STRING - e.g. the " +
                "docker-compose Postgres from local-dev.md.");

        services.AddCalendarPostgresPersistence(connectionString);

        // The single wall-clock-to-instant bridge (adr/0049). Registered for every host, not only
        // the Worker: the manual-edit handlers convert too, and they are the API's business.
        services.AddCalendarTimeZoneResolution();

        // `20-03`: Redis, for the one thing this product needs from it - the booking endpoint's two
        // rate-limit buckets. Registered for every host even though only the API takes a check: the
        // hosts differ in what they run, not in what the product is (adr/0013), and a connection is
        // opened lazily on first use, so the Worker pays nothing for the registration.
        services.AddCalendarRateLimiting(configuration);

        services.AddScoped<MaterializeAvailabilityHandler>();
        services.AddScoped<DeleteDayOffHandler>();
        services.AddScoped<EditDayBoundaryHandler>();

        // Bound and validated at startup (naming-and-structure.md). Handlers take the plain value,
        // never IOptions<T> - Application must not know how a host binds configuration, which is the
        // same call ago-chat's own rate-limit options make at every call site.
        services.AddOptions<BookingOptions>()
            .Bind(configuration.GetSection(BookingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BookingOptions>>().Value);

        services.AddOptions<BookingRateLimitOptions>()
            .Bind(configuration.GetSection(BookingRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BookingRateLimitOptions>>().Value);

        services.AddScoped<BookEventHandler>();

        // `20-04`: the three operator-facing transitions and the shared queue they act on.
        services.AddScoped<RejectBookingHandler>();
        services.AddScoped<CancelBookingHandler>();
        services.AddScoped<MarkNoShowHandler>();
        services.AddScoped<GetPendingBookingsForTenantHandler>();

        // `20-06`. Registered in the module rather than in Ago.Calendar.Api, even though only the Api
        // host has routes for them: adr/0013's split is by failure profile, and the hosts differ in
        // what they *run*, never in what the product is. A scoped registration nobody resolves costs
        // the Worker nothing.
        services.AddScoped<EmbedScopeResolver>();
        services.AddScoped<GetBookingSurfaceHandler>();
        services.AddScoped<GetBookableWorkersHandler>();
        services.AddScoped<GetOpenSlotsHandler>();
        services.AddScoped<CheckTenantOriginHandler>();

        // `20-07`: the chat entry point. Bound at startup like every other options class here, but
        // deliberately *not* given a hard `.Validate(...)` the way `Operator:Authority` gets one -
        // see ChatModuleTaskOptions's own remarks on why an unconfigured value is a per-request
        // chat_module_task.not_configured rejection rather than a reason to refuse to boot the whole
        // host. `Operator:Authority`'s no-fallback rule exists because a host with no authority has
        // no authentication at all; leaving ChatModule:* unset disables one feature, and every other
        // route - including the widget's own booking flow - keeps working.
        services.AddOptions<ChatModuleTaskOptions>()
            .Bind(configuration.GetSection(ChatModuleTaskOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<ChatModuleTaskOptions>>().Value);

        services.AddScoped<StartModuleTaskHandler>();
        services.AddScoped<ReplyToModuleTaskHandler>();

        services.AddScoped<GetTenantConfigurationHandler>();
        services.AddScoped<CreateCalendarHandler>();
        services.AddScoped<UpdateCalendarHandler>();
        services.AddScoped<CreateServiceHandler>();
        services.AddScoped<CreateWorkerHandler>();

        // `20-13`: the rest of the worker CRUD surface - GET /workers, GET /workers/{id},
        // PUT /workers/{id}, DELETE /workers/{id}.
        services.AddScoped<ListWorkersForTenantHandler>();
        services.AddScoped<GetWorkerHandler>();
        services.AddScoped<UpdateWorkerHandler>();
        services.AddScoped<DeleteWorkerHandler>();

        // `20-14`: a worker's own schedule template - GET/PUT /workers/{id}/schedule.
        services.AddScoped<GetWorkerScheduleHandler>();
        services.AddScoped<SaveWorkerScheduleHandler>();

        services.AddScoped<AddWorkingHoursRuleHandler>();
        services.AddScoped<SetAllowedOriginsHandler>();
        services.AddScoped<RegisterTenantHandler>();

        // `20-12`: the second role, moving an operator on/off it, and the tenant contacts report.
        services.AddScoped<CreateRoleHandler>();
        services.AddScoped<ListRolesForTenantHandler>();
        services.AddScoped<ListOperatorsForTenantHandler>();
        services.AddScoped<GrantOperatorRoleHandler>();
        services.AddScoped<RevokeOperatorRoleHandler>();
        services.AddScoped<GetTenantContactsHandler>();

        // `20-15`: the materialised slot view.
        services.AddScoped<GetWorkerSlotsHandler>();

        // `20-16`: the one deliberate exception to the forward-only cursor - preview what a re-cut
        // would destroy, then apply it. RecutConfirmHandler takes CancelBookingHandler itself as a
        // constructor dependency so cancellation goes through the ordinary use case rather than a
        // second implementation of it; both are scoped, so both resolve against the same DbContext
        // within one request.
        services.AddScoped<RecutPreviewHandler>();
        services.AddScoped<RecutConfirmHandler>();
    }
}
