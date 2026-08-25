using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class WorkingHoursRuleConfiguration : IEntityTypeConfiguration<WorkingHoursRule>
{
    public void Configure(EntityTypeBuilder<WorkingHoursRule> builder)
    {
        builder.ToTable("working_hours_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").HasConversion(IdConverters.WorkingHoursRule).ValueGeneratedNever();
        builder.Property(r => r.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.Worker);
        builder.Property(r => r.CalendarId).HasColumnName("calendar_id").HasConversion(IdConverters.Calendar);

        // Stored as its name, not its ordinal - the same choice ago-chat makes for every enum it
        // persists. An ordinal is unreadable in psql and silently re-numbers if the enum is ever
        // reordered; DayOfWeek's ordinals are additionally a trap, because Sunday is 0 in .NET and
        // 1 in the ISO week most humans think in.
        builder.Property(r => r.DayOfWeek).HasColumnName("day_of_week").HasConversion<string>().HasMaxLength(9);

        // `time` (without time zone), which is exactly right and the one place in this schema where
        // a naive value is correct: these are wall-clock readings in the calendar's own zone, not
        // instants (date-and-time.md's own carve-out - "DateOnly/TimeOnly for genuine calendar
        // values"). Npgsql maps TimeOnly to `time` by default; named explicitly because the whole
        // time model of this product turns on this column not being a timestamptz.
        builder.Property(r => r.StartsAt).HasColumnName("starts_at").HasColumnType("time");
        builder.Property(r => r.EndsAt).HasColumnName("ends_at").HasColumnType("time");

        builder.HasOne<Worker>().WithMany().HasForeignKey(r => r.WorkerId);
        builder.HasOne<BookingCalendar>().WithMany().HasForeignKey(r => r.CalendarId);

        // IWorkingHoursRuleRepository.ListForCalendarAsync's index: `20-02` reads every rule of one
        // calendar per materialisation run, ordered by worker so the batches it builds are already
        // grouped.
        builder.HasIndex(r => new { r.CalendarId, r.WorkerId })
            .HasDatabaseName("ix_working_hours_rules_calendar_worker");
    }
}
