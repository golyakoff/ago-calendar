using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class WorkerScheduleConfiguration : IEntityTypeConfiguration<WorkerSchedule>
{
    public void Configure(EntityTypeBuilder<WorkerSchedule> builder)
    {
        builder.ToTable("worker_schedules");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasConversion(IdConverters.WorkerSchedule).ValueGeneratedNever();
        builder.Property(s => s.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.Worker);

        // Stored as its name, not its ordinal - the same choice WorkingHoursRuleConfiguration makes
        // for DayOfWeek, for the same reason: an ordinal is unreadable in psql and silently
        // re-numbers if the enum is ever reordered.
        builder.Property(s => s.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);

        builder.Property(s => s.CycleAnchor).HasColumnName("cycle_anchor");
        builder.Property(s => s.CycleWorkingDays).HasColumnName("cycle_working_days");
        builder.Property(s => s.CycleRestDays).HasColumnName("cycle_rest_days");

        // `time` (without time zone) - the same wall-clock convention WorkingHoursRuleConfiguration
        // uses for its own StartsAt/EndsAt, and for the same reason: these are readings on a clock in
        // the worker's calendar's own zone, not instants.
        builder.Property(s => s.CycleStartsAt).HasColumnName("cycle_starts_at").HasColumnType("time");
        builder.Property(s => s.CycleEndsAt).HasColumnName("cycle_ends_at").HasColumnType("time");

        builder.Property(s => s.SlotMinutes).HasColumnName("slot_minutes");
        builder.Property(s => s.BufferMinutes).HasColumnName("buffer_minutes");

        // `20-18`: the tenant's own call on whether a multi-slot run's internal buffers count toward
        // satisfying a service's duration. Defaults true at the column too (not only in the aggregate's
        // own field initializer), so a backfilled row from this item's migration - and any future
        // insert that forgets to set it explicitly - lands on the same default WorkerSchedule itself
        // states.
        builder.Property(s => s.BuffersCountTowardServiceDuration)
            .HasColumnName("buffers_count_toward_service_duration")
            .HasDefaultValue(true);

        builder.Property(s => s.HorizonDays).HasColumnName("horizon_days");
        builder.Property(s => s.MaterializeFrom).HasColumnName("materialize_from");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // One schedule per worker, structurally: the same unique constraint
        // ux_tenants_public_key's own remarks describe elsewhere in this schema for "the aggregate
        // already refuses this, and the storage layer refuses it too, for the caller that goes around
        // the aggregate" - here that caller is a concurrent insert racing another one for the same
        // worker, which only a database-level constraint can arbitrate.
        builder.HasIndex(s => s.WorkerId).IsUnique().HasDatabaseName("ux_worker_schedules_worker_id");

        builder.HasOne<Worker>().WithMany().HasForeignKey(s => s.WorkerId).OnDelete(DeleteBehavior.Cascade);
    }
}
