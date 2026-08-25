using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("services");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasConversion(IdConverters.Service).ValueGeneratedNever();
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // Whole minutes in an int, not a Postgres interval - see IdConverters.DurationMinutes.
        builder.Property(s => s.Duration)
            .HasColumnName("duration_minutes")
            .HasConversion(IdConverters.DurationMinutes);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
    }
}
