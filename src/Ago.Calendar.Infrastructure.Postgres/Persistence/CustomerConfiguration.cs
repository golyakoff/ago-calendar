using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasConversion(IdConverters.Customer).ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);

        builder.Property(c => c.Phone)
            .HasColumnName("phone")
            .HasMaxLength(16)
            .HasConversion(IdConverters.Phone)
            .IsRequired();

        builder.Property(c => c.DisplayName).HasColumnName("display_name").HasMaxLength(200);
        builder.Property(c => c.Notes).HasColumnName("notes").HasMaxLength(4000);
        builder.Property(c => c.NoShowCount).HasColumnName("no_show_count");
        builder.Property(c => c.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType("timestamptz");
        builder.Property(c => c.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId);

        // The lead card's identity rule, at the storage level. (tenant_id, phone), never phone
        // alone: the same person booking at two shops is two cards, and one tenant's notes must
        // never reach another's console.
        //
        // This index is also `20-03`'s find-or-create backstop. Two simultaneous first-time bookings
        // from the same number both find nothing and both insert; this is what makes the loser fail
        // instead of creating a second card - the same "the index is the storage backstop, not the
        // primary mechanism" division adr/0019 already draws for AGO Chat's message sequence.
        builder.HasIndex(c => new { c.TenantId, c.Phone })
            .IsUnique()
            .HasDatabaseName("ux_customers_tenant_phone");
    }
}
