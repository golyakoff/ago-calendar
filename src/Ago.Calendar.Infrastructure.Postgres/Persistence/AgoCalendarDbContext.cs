using Ago.Calendar.Domain;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// This product's own database - not a schema inside AGO Chat's (adr/0027). Nothing here can reach a
/// <c>Ago.Chat.*</c> table, and the arch tests prove the assembly cannot even reference the types.
/// </summary>
public sealed class AgoCalendarDbContext(DbContextOptions<AgoCalendarDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<BookingCalendar> Calendars => Set<BookingCalendar>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<WorkingHoursRule> WorkingHoursRules => Set<WorkingHoursRule>();
    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgoCalendarDbContext).Assembly);

        // adr/0017: the one line a product's DbContext needs to opt into the shared outbox/inbox
        // schema. Ago.Platform.Persistence.Postgres owns the table shape, and this is the second
        // product to take it unchanged - which is the first actual evidence that the outbox
        // generalisation was real rather than an AGO-Chat-shaped guess.
        //
        // The tables arrive with no writer in this product yet: `20-05`'s SMS integration event is
        // the first, and CLAUDE.md rule 4 means it will be written in the same transaction as the
        // booking that caused it. Creating them now costs one migration and means that item does not
        // have to change the schema to send its first message.
        modelBuilder.ApplyOutboxInboxConfiguration();
    }
}
