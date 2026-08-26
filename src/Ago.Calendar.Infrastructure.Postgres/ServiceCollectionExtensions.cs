using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
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

        // adr/0017: the platform's own generic outbox/inbox, bound to this product's context. The
        // tables exist from this migration onward; the first writer is `20-05`.
        services.AddOutboxInbox<AgoCalendarDbContext>();

        return services;
    }
}
