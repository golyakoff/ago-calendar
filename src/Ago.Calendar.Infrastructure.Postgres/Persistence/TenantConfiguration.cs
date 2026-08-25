using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(IdConverters.Tenant).ValueGeneratedNever();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // timestamptz, from IClock, never a database default - date-and-time.md rule 1 and rule 3
        // together: the value is UTC and it comes from the application, so a test can control it.
        // Npgsql maps DateTimeOffset to timestamptz by default; stated here because the mapping is
        // the guarantee, not a convention anyone should have to look up.
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    }
}
