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

        // `20-18`: which booking this row belongs to - another event's own id, the run's anchor. See
        // Event.BookingId's own remarks for why this column rather than a second `bookings` table.
        builder.Property(e => e.BookingId).HasColumnName("booking_id").HasConversion(IdConverters.NullableEvent);

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

        // `20-02`: every question the materialiser and the two manual-edit handlers ask is "what is
        // on this worker's day", so the index is (calendar_id, worker_id, local_date) in exactly
        // that order - two equalities then a range, which is the only order a B-tree can serve all
        // three of them from.
        //
        // Not filtered, unlike the two above, and that is the interesting half. A partial index on
        // `status = 'Available'` would be smaller and would answer nothing this item asks: the
        // non-destructive rule turns on whether a day has *any* row, cancelled and booked included,
        // so a filter would hide precisely the rows whose presence is the decision. `20-01`'s own
        // ix_events_available cannot be reused for the same reason.
        //
        // It also carries the day-off delete (`ReplaceDayAsync`), which is the query
        // adr/0049 stored `local_date` for rather than deriving it: `AT TIME ZONE` in the predicate
        // is non-sargable, so this index would be unusable and every day-scoped edit would scan.
        builder.HasIndex(e => new { e.CalendarId, e.WorkerId, e.LocalDate })
            .HasDatabaseName("ix_events_worker_day");

        // `20-18`: every read model that groups a multi-slot run back into one booking
        // (PendingBookingReadStore, IEventRepository.ListByBookingIdAsync) asks "every row with this
        // booking_id", so the index is on the column alone. Partial - `WHERE booking_id IS NOT NULL` -
        // because an Available or Blocked row never carries one, and indexing that majority of the
        // table for a predicate no query ever asks would only cost writes for no reader.
        builder.HasIndex(e => e.BookingId)
            .HasDatabaseName("ix_events_booking_id")
            .HasFilter("booking_id IS NOT NULL");

        // A self-referencing foreign key - booking_id names another row of this same table, the run's
        // anchor. Real referential integrity at negligible cost: every write that sets it does so
        // inside the same transaction as the anchor row's own claim (BookingStore.TryBookAsync), so
        // the constraint can never actually reject a legitimate write, but it does mean a future bug
        // that pointed booking_id at a nonexistent id fails loudly at the database rather than
        // silently producing a group lookup that finds nothing. No navigation property either
        // direction: Event has no "the other rows of my own run" collection to keep in sync, the same
        // reason the four HasOne calls above declare no inverse navigation on their principal types.
        builder.HasOne<Event>().WithMany().HasForeignKey(e => e.BookingId).HasPrincipalKey(e => e.Id)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
