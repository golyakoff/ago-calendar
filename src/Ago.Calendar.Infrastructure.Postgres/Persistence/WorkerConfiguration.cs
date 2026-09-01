using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        builder.ToTable("workers");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasColumnName("id").HasConversion(IdConverters.Worker).ValueGeneratedNever();
        builder.Property(w => w.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(w => w.LastName).HasColumnName("last_name").HasMaxLength(100).IsRequired();
        builder.Property(w => w.FirstName).HasColumnName("first_name").HasMaxLength(100).IsRequired();
        builder.Property(w => w.MiddleName).HasColumnName("middle_name").HasMaxLength(100);
        builder.Property(w => w.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(w => w.DisplayNameIsCustom).HasColumnName("display_name_is_custom");
        builder.Property(w => w.IsActive).HasColumnName("is_active");
        builder.Property(w => w.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(w => w.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId);

        // Both joins hang off this aggregate - see Worker's own remarks for why the invariants they
        // carry are statements about a worker rather than about a calendar or a service. EF is
        // pointed at the private backing fields directly, so loading never runs JoinCalendar/Offer.
        builder.Ignore(w => w.Calendars);
        builder.HasMany<CalendarMembership>("_calendars")
            .WithOne()
            .HasForeignKey(m => m.WorkerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation("_calendars").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(w => w.Services);
        builder.HasMany<ServiceOffering>("_services")
            .WithOne()
            .HasForeignKey(o => o.WorkerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation("_services").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
