using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class ChatBookingTaskConfiguration : IEntityTypeConfiguration<ChatBookingTask>
{
    public void Configure(EntityTypeBuilder<ChatBookingTask> builder)
    {
        builder.ToTable("chat_booking_tasks");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasColumnName("id")
            .HasConversion(IdConverters.ChatBookingTask)
            .ValueGeneratedNever();

        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(t => t.CalendarId).HasColumnName("calendar_id").HasConversion(IdConverters.Calendar);
        builder.Property(t => t.ServiceId).HasColumnName("service_id").HasConversion(IdConverters.NullableService);
        builder.Property(t => t.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.NullableWorker);

        // `25-33`: a plain nullable DateOnly column, the same "date" column type Event.LocalDate
        // already uses (EventConfiguration's own precedent) - null until the date round's own
        // ChooseDate runs, and never cleared afterward (ReopenForSlotChoice's own remarks).
        builder.Property(t => t.SelectedDate).HasColumnName("selected_date").HasColumnType("date");

        builder.Property(t => t.EventId).HasColumnName("event_id").HasConversion(IdConverters.NullableEvent);

        // Raw text, not IdConverters.Phone's PhoneNumber - unlike Event.CustomerId, this column is a
        // record of what the visitor typed while the task was in flight, not the normalised value
        // BookEventHandler's own PhoneNumber constructor produces. Re-validating it here would just
        // be BookEventHandler's own check performed a second time on data this column does not use
        // for anything besides "what did we collect".
        builder.Property(t => t.Phone).HasColumnName("phone").HasMaxLength(32);

        builder.Property(t => t.State).HasColumnName("state").HasConversion<string>().HasMaxLength(24);

        // `25-32`: the wire value that produced the *current* State - see the domain property's own
        // remarks. 64 covers a canonical GUID string (36 chars) with room to spare; the only other
        // shape this ever holds is a phone number, already capped at 32 by the column just above.
        builder.Property(t => t.LastAppliedValue).HasColumnName("last_applied_value").HasMaxLength(64);

        builder.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId);
        builder.HasOne<BookingCalendar>().WithMany().HasForeignKey(t => t.CalendarId);

        // No index beyond the primary key: every read this product performs on this table is
        // GetByIdAsync, a single-row primary-key lookup - see IChatBookingTaskStore's own remarks on
        // why there is no compare-and-set and therefore no query shape to serve beyond that one.
    }
}
