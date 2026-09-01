using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
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

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        // `20-12`: the second role and every role-assignment change go through this - see
        // IRoleRepository's own remarks for why it did not exist before this item.
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IBookingCalendarRepository, BookingCalendarRepository>();
        services.AddScoped<IWorkerRepository, WorkerRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IWorkingHoursRuleRepository, WorkingHoursRuleRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        // `20-03`: the booking write - the compare-and-set claim and the lead-card upsert, in one
        // transaction. Scoped like every other adapter here, because it holds the DbContext whose
        // connection both statements run on.
        services.AddScoped<IBookingStore, BookingStore>();

        // `20-04`: the sweep's one transactional step, and the permission resolution every
        // operator-facing handler goes through. Scoped like the rest - both hold the DbContext.
        services.AddScoped<IExpiredBookingConfirmer, ExpiredBookingConfirmer>();
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
        services.AddScoped<ITenantProvisioningStore, TenantProvisioningStore>();

        // `20-07`: the chat module's own orchestration state. Plain load/save, unlike IBookingStore -
        // see IChatBookingTaskStore's own remarks.
        services.AddScoped<IChatBookingTaskStore, ChatBookingTaskStore>();

        // adr/0017: the platform's own generic outbox/inbox, bound to this product's context. The
        // tables exist from this migration onward; the first writer is `20-05`.
        services.AddOutboxInbox<AgoCalendarDbContext>();

        return services;
    }
}
