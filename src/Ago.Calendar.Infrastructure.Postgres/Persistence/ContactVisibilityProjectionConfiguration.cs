using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class ContactVisibilityProjectionConfiguration
    : IEntityTypeConfiguration<ContactVisibilityProjectionRecord>
{
    public void Configure(EntityTypeBuilder<ContactVisibilityProjectionRecord> builder)
    {
        builder.ToTable("contact_visibility_projections");

        builder.HasKey(r => r.TenantId);
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant)
            .ValueGeneratedNever();

        builder.Property(r => r.Rung).HasColumnName("rung").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
    }
}
