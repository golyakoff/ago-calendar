using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class CalendarMembershipConfiguration : IEntityTypeConfiguration<CalendarMembership>
{
    public void Configure(EntityTypeBuilder<CalendarMembership> builder)
    {
        builder.ToTable("calendar_workers");
        builder.HasKey(m => new { m.CalendarId, m.WorkerId });
        builder.Property(m => m.CalendarId).HasColumnName("calendar_id").HasConversion(IdConverters.Calendar);
        builder.Property(m => m.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.Worker);

        builder.HasOne<BookingCalendar>().WithMany().HasForeignKey(m => m.CalendarId);

        // Note what is *not* here: a unique index on worker_id alone, which would make v1's
        // one-calendar-per-worker limit a storage rule. Left out on purpose - the limit is v1
        // policy the aggregate enforces (Worker.JoinCalendar), and widening it later must not
        // require a migration to undo a constraint that was never about correctness. The table shape
        // is already the M:N one the widened model needs.
        //
        // The reverse case, the no-overlap rule on `events`, *is* a storage constraint, and the
        // difference is the point: that one cannot be enforced by an aggregate at all, this one can.
    }
}
