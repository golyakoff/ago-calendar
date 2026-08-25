using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class ServiceOfferingConfiguration : IEntityTypeConfiguration<ServiceOffering>
{
    public void Configure(EntityTypeBuilder<ServiceOffering> builder)
    {
        builder.ToTable("worker_services");
        builder.HasKey(o => new { o.WorkerId, o.ServiceId });
        builder.Property(o => o.WorkerId).HasColumnName("worker_id").HasConversion(IdConverters.Worker);
        builder.Property(o => o.ServiceId).HasColumnName("service_id").HasConversion(IdConverters.Service);

        builder.HasOne<Service>().WithMany().HasForeignKey(o => o.ServiceId);
    }
}
