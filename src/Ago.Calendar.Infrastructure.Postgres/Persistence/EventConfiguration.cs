using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdConverters.Event).ValueGeneratedNever();
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(e => e.CalendarId).HasColumnName("calendar_id").HasConversion(IdConverters.Calendar);
        builder.Property(e => e.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.Worker);
        builder.Property(e => e.ServiceId).HasColumnName("service_id").HasConversion(IdConverters.NullableService);
        builder.Property(e => e.CustomerId).HasColumnName("customer_id").HasConversion(IdConverters.NullableCustomer);

        // timestamptz - absolute instants, the only kind of time this table stores. Contrast
        // working_hours_rules, whose `time` columns are wall clock; the two are converted into each
        // other exactly once, at materialisation, and never compared directly.
        builder.Property(e => e.StartsAt).HasColumnName("starts_at").HasColumnType("timestamptz");
        builder.Property(e => e.EndsAt).HasColumnName("ends_at").HasColumnType("timestamptz");

        // `date` - the business-local day, already resolved through the calendar's zone. See
        // Event.LocalDate for why it is stored rather than derived with AT TIME ZONE at query time.
        builder.Property(e => e.LocalDate).HasColumnName("local_date").HasColumnType("date");

        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.ConfirmationDeadline)
            .HasColumnName("confirmation_deadline")
            .HasColumnType("timestamptz");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

        // Computed from the two columns above, never mapped - see Event.Slot.
        builder.Ignore(e => e.Slot);

        // In-memory-only facts. The outbox is the wire (adr/0005/0017); a domain event is not a
        // stored column, and mapping one would be the "domain events are not wire contracts" rule
        // broken at the schema level.
        builder.Ignore(e => e.DomainEvents);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId);
        builder.HasOne<BookingCalendar>().WithMany().HasForeignKey(e => e.CalendarId);
        builder.HasOne<Worker>().WithMany().HasForeignKey(e => e.WorkerId);
        builder.HasOne<Service>().WithMany().HasForeignKey(e => e.ServiceId);
        builder.HasOne<Customer>().WithMany().HasForeignKey(e => e.CustomerId);

        // Postgres's own system column, not one we maintain: EF bumps and checks it on every UPDATE,
        // which is optimistic concurrency with no migration-visible column to keep in sync by hand
        // (the same call `1-04` made for ago-chat's conversations). This is what makes a
        // load-mutate-save claim safe: two callers who both loaded the row as Available both pass
        // Event.Claim in memory, and exactly one of their saves commits.
        builder.Property<uint>("xmin").IsRowVersion();

        // The availability index `20-03`'s booking claim reads through, and the direct analogue of
        // ix_conversations_waiting: a partial index on the one status a customer can act on, so the
        // index stays proportional to what is bookable rather than to everything that has ever been
        // booked. Ordered by starts_at because a customer asks for "the next free slots", which is a
        // range scan from now forward, not a lookup.
        //
        // The filter is written with the CLR enum's own name because the column stores the enum name
        // (HasConversion<string> above), and the two must agree exactly or the index silently serves
        // nothing.
        builder.HasIndex(e => new { e.CalendarId, e.StartsAt })
            .HasDatabaseName("ix_events_available")
            .HasFilter("status = 'Available'");

        // `20-04`'s two sweeps, and the reason they are one index rather than two: the auto-confirm
        // job asks "which pending rows are past their deadline" and the operator queue asks "what is
        // pending for this tenant", and both are answered by a partial index over the same small,
        // short-lived set of PendingConfirmation rows.
        builder.HasIndex(e => new { e.TenantId, e.ConfirmationDeadline })
            .HasDatabaseName("ix_events_pending_confirmation")
            .HasFilter("status = 'PendingConfirmation'");
    }
}
