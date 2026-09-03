using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Infrastructure.Postgres.Schema;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Infrastructure.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The one place a concrete adapter is named. clean-architecture.md puts DI wiring in the host,
    /// and this method is how that stays true without the host learning nine class names: the host
    /// calls one method whose name says which technology it is choosing, and everything behind it
    /// stays internal to this project's own decisions.
    ///
    /// <para>Scoped, not singleton: <c>DbContext</c> is not thread-safe and a unit of work is
    /// per-request/per-message by construction. Every repository below is scoped for the same reason
    /// - they hold the context, so their lifetime is its lifetime.</para>
    /// </summary>
    public static IServiceCollection AddCalendarPostgresPersistence(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AgoCalendarDbContext>(options => options.UseNpgsql(connectionString));

        // `20-21`: the read half every serving host needs to run SchemaVersionGuard - the apply half
        // (SchemaMigrationApplier) is deliberately not registered here, the same way `8-08` keeps it
        // out of Ago.Chat's own DI graph: only Ago.Calendar.Migrator constructs it, by hand, and
        // SchemaMigrationTests is what makes "no host may apply a migration" a fact rather than a
        // convention.
        services.AddScoped<SchemaVersionCheck>();

        services.AddScoped<ITenantRepository, TenantRepository>();
        // `22-05`/`adr/0093`: IOperatorRepository/IRoleRepository are gone - there is no local
        // `operators`/`roles` table left to back them. IRoleAssignmentProjectionStore, registered
        // below, is what a permission check and the claims transformation read instead.
        services.AddScoped<IBookingCalendarRepository, BookingCalendarRepository>();
        services.AddScoped<IWorkerRepository, WorkerRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IWorkingHoursRuleRepository, WorkingHoursRuleRepository>();
        // `20-14`: a worker's own schedule template - the materialiser's other input alongside
        // IWorkingHoursRuleRepository.
        services.AddScoped<IWorkerScheduleRepository, WorkerScheduleRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        // `20-03`: the booking write - the compare-and-set claim and the lead-card upsert, in one
        // transaction. Scoped like every other adapter here, because it holds the DbContext whose
        // connection both statements run on.
        services.AddScoped<IBookingStore, BookingStore>();

        // `20-04`: the sweep's one transactional step, and the permission resolution every
        // operator-facing handler goes through. Scoped like the rest - both hold the DbContext.
        services.AddScoped<IExpiredBookingConfirmer, ExpiredBookingConfirmer>();

        // `22-05`/`adr/0093`: the projection replicated from AGO Chat's own `RoleAssignmentsChanged` -
        // the one thing both a permission check and OperatorIdentityClaimsTransformation now read.
        // Same DbContext, same "Infrastructure adapter behind an Application port" shape every other
        // repository here already uses.
        services.AddScoped<IRoleAssignmentProjectionStore, RoleAssignmentProjectionStore>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();

        // adr/0004's read side. Its own NpgsqlDataSource rather than the DbContext's connection: a
        // read model that shared a write context would inherit its change tracker and any ambient
        // transaction, and a queue screen has no business inside a write transaction. Singleton
        // because a data source is a pool, not a connection.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<IPendingBookingReadStore, PendingBookingReadStore>();

        // `20-12`: the tenant contacts report's own read side, the same NpgsqlDataSource singleton
        // above rather than a second pool - PendingBookingReadStore's own remarks on why a read store
        // never shares the write context still apply, but there is no reason to open a second
        // connection pool for a second read store.
        services.AddScoped<IContactsReadStore, ContactsReadStore>();

        // `20-15`: the materialised slot view's own read side - the same shared NpgsqlDataSource
        // singleton, for the identical reason ContactsReadStore's own remark gives.
        services.AddScoped<IWorkerSlotReadStore, WorkerSlotReadStore>();

        // `20-06`: the public booking surface's own read side, and the multi-aggregate provisioning
        // write ITenantRepository's remarks predicted would be the first use case to need one.
        services.AddScoped<IBookingSurfaceReadStore, BookingSurfaceReadStore>();
        // `22-05`/`adr/0093`: ITenantProvisioningStore is gone - provisioning a tenant is now a
        // single-aggregate write, so RegisterTenantHandler uses ITenantRepository directly.

        // `20-07`: the chat module's own orchestration state. Plain load/save, unlike IBookingStore -
        // see IChatBookingTaskStore's own remarks.
        services.AddScoped<IChatBookingTaskStore, ChatBookingTaskStore>();

        // `20-10`: the public widget's own phone-verification aggregate, plus its two CSPRNG-backed
        // generators - code and proof token. Scoped like every other aggregate repository here; the
        // generators hold no state and could be singletons, but are registered scoped for the same
        // "everything behind this method is an implementation detail, not a decision the host makes"
        // uniformity the rest of this method already follows.
        services.AddScoped<IPendingPhoneVerificationRepository, PendingPhoneVerificationRepository>();
        services.AddScoped<IPhoneVerificationCodeGenerator, PhoneVerificationCodeGenerator>();
        services.AddScoped<IPhoneVerificationProofTokenGenerator, PhoneVerificationProofTokenGenerator>();

        // `22-04`: adr/0065's registry, this product's own consuming half - see
        // ChatModuleRegistration's own remarks.
        services.AddScoped<IChatModuleRegistrationRepository, ChatModuleRegistrationRepository>();

        // adr/0017: the platform's own generic outbox/inbox, bound to this product's context. The
        // tables exist from this migration onward; the first writer is `20-05`.
        services.AddOutboxInbox<AgoCalendarDbContext>();

        return services;
    }
}
