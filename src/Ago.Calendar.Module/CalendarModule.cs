using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.ChatModuleTask;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Application.UseCases.Cors;
using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Application.UseCases.PhoneVerification;
using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Application.UseCases.WorkerSlots;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Schema;
using Ago.Calendar.Infrastructure.Redis;
using Ago.Calendar.Infrastructure.Time;
using Ago.Calendar.Module.PhoneVerification;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
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

        // `20-21`: bound here, alongside the connection string above, so that every host loading this
        // module - Api and Worker alike - carries the option SchemaGuardHostExtensions reads. Both
        // hosts call EnsureSchemaIsCurrentAsync in their own Program.cs; Ago.Calendar.Migrator does not
        // load this module at all, and does not need this - it is the thing the guard waits for.
        services
            .AddOptions<SchemaGuardOptions>()
            .Bind(configuration.GetSection(SchemaGuardOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The single wall-clock-to-instant bridge (adr/0049). Registered for every host, not only
        // the Worker: the manual-edit handlers convert too, and they are the API's business.
        services.AddCalendarTimeZoneResolution();

        // `20-03`: Redis, for the one thing this product needs from it - the booking endpoint's two
        // rate-limit buckets. Registered for every host even though only the API takes a check: the
        // hosts differ in what they run, not in what the product is (adr/0013), and a connection is
        // opened lazily on first use, so the Worker pays nothing for the registration.
        services.AddCalendarRateLimiting(configuration);

        // `22-05`/`adr/0093`: this product's first broker connection - `RoleAssignmentsChangedConsumer`
        // (`Ago.Calendar.Worker`) is the one caller. Registered for every host, the same "hosts differ
        // in what they run, not in what the product is" shape `AddCalendarRateLimiting` above already
        // follows: `Ago.Calendar.Api` never resolves `IEventConsumer`, so it pays nothing beyond the
        // registration itself. `Messaging:RabbitMq:*` must name the same broker/vhost `ago-chat`'s own
        // Worker publishes to - a deploy-time configuration fact, not something this module can assert.
        services.AddRabbitMqMessaging(configuration);

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

        // `20-10`: the public widget's own phone-verification primitive. Bound and registered before
        // BookEventHandler, which now takes PhoneVerificationAssertionResolver as a constructor
        // dependency.
        services.AddOptions<PhoneVerificationOptions>()
            .Bind(configuration.GetSection(PhoneVerificationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<PhoneVerificationOptions>>().Value);

        services.AddOptions<PhoneVerificationRateLimitOptions>()
            .Bind(configuration.GetSection(PhoneVerificationRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PhoneVerificationRateLimitOptions>>().Value);

        // FakePhoneVerificationSender is the *only* IPhoneVerificationSender this module registers -
        // unconditionally, not behind an `if (configured)` branch and not the
        // `UnconfiguredPhoneVerificationSender`-shaped "throw" `ago-chat` uses for the identical port.
        // See that type's own remarks for why. Registered once, as a singleton, under both the port and
        // its own concrete type: the port is what every real caller depends on, and the concrete type
        // is what `PhoneVerificationDevEndpoints`' own dev-only surface resolves directly to read back
        // the last code it captured - a capability deliberately absent from the port itself. Singleton
        // because it is stateless towards its real callers and its own in-memory capture is a
        // dev/demo convenience explicitly not meant to survive a restart or span replicas.
        services.AddSingleton<FakePhoneVerificationSender>();
        services.AddSingleton<IPhoneVerificationSender>(
            provider => provider.GetRequiredService<FakePhoneVerificationSender>());

        services.AddScoped<PhoneVerificationAssertionResolver>();
        services.AddScoped<InitiatePhoneVerificationHandler>();
        services.AddScoped<ConfirmPhoneVerificationHandler>();

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

        // `20-07`/`22-04`: the chat entry point. No more options class to bind here - this
        // deployment no longer answers for one statically configured tenant
        // (`ChatModuleTaskOptions`, removed by `22-04`) and no longer checks calls against one
        // deployment-wide secret (`ModuleCallCredentialOptions`, also removed). Both are replaced by
        // Ago.Calendar.Domain.ChatModuleRegistration, a per-tenant database row read fresh on every
        // call - see that type's own remarks and HmacModuleCallCredentialValidator's own remarks.
        // Scoped, not singleton, for the identical reason every other adapter holding a
        // DbContext-backed dependency here is.
        services.AddScoped<IModuleCallCredentialValidator, HmacModuleCallCredentialValidator>();

        services.AddScoped<StartModuleTaskHandler>();
        services.AddScoped<ReplyToModuleTaskHandler>();

        // `22-11`: provisioning - the write that had nothing outside a test calling it. A different
        // secret and a different mechanism from the credential validator just above (see
        // IModuleProvisioningAuthenticator's own remarks for why); bound and registered here rather
        // than in Ago.Calendar.Api's own Program.cs because, like IModuleCallCredentialValidator
        // beside it, ChatModuleRegistrationErrors's four handlers are plain Application types the
        // Worker's DI graph can build even though only the Api host ever maps a route to them - the
        // identical "a scoped registration nobody resolves costs the Worker nothing" reasoning this
        // file already gives for EmbedScopeResolver.
        services.AddOptions<ModuleProvisioningOptions>()
            .Bind(configuration.GetSection(ModuleProvisioningOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<IModuleProvisioningAuthenticator, SharedSecretModuleProvisioningAuthenticator>();

        services.AddScoped<Application.UseCases.ChatModuleRegistration.RegisterChatModuleHandler>();
        services.AddScoped<Application.UseCases.ChatModuleRegistration.RotateChatModuleCredentialHandler>();
        services.AddScoped<Application.UseCases.ChatModuleRegistration.RevokeChatModuleRegistrationHandler>();
        services.AddScoped<Application.UseCases.ChatModuleRegistration.GetChatModuleRegistrationStatusHandler>();

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

        // `22-05`/`adr/0093`: CreateRoleHandler/ListRolesForTenantHandler/ListOperatorsForTenantHandler/
        // GrantOperatorRoleHandler/RevokeOperatorRoleHandler/InviteOperatorHandler are gone - there is
        // no local `operators`/`roles` table left for any of them to manage. The tenant contacts
        // report stays; it reads `customers`, not identity.
        services.AddScoped<GetTenantContactsHandler>();

        // `22-14`/`adr/0100`: the switcher's own read - "which tenants may I act in here". Not an
        // identity-management endpoint of the kind `22-05` deleted above: it manages nothing and
        // grants nothing, it reports what the projection `ago-chat` replicates already says.
        services.AddScoped<Application.UseCases.Tenancies.ListMyTenanciesHandler>();

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
