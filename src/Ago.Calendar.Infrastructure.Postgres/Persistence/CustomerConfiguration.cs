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

        // `23-59`/`adr/0147`: stored as the CLR member name via EF's default string conversion, not an
        // ordinal - the same reasoning every other closed-vocabulary enum in this codebase's own
        // remarks give (an ordinal makes reordering the enum a silent data corruption).
        builder.Property(c => c.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(CustomerSource.Booking)
            .IsRequired();
        builder.Property(c => c.SourceContactId).HasColumnName("source_contact_id");

        builder.Property(c => c.DisplayName).HasColumnName("display_name").HasMaxLength(200);
        builder.Property(c => c.Notes).HasColumnName("notes").HasMaxLength(4000);
        builder.Property(c => c.PhoneVerifiedAt).HasColumnName("phone_verified_at").HasColumnType("timestamptz");
        builder.Property(c => c.PhoneConfirmedByOperatorAt)
            .HasColumnName("operator_confirmed_phone_at")
            .HasColumnType("timestamptz");
        builder.Property(c => c.NoShowCount).HasColumnName("no_show_count");
        builder.Property(c => c.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType("timestamptz");
        builder.Property(c => c.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");

        // `23-60`/`adr/0147`: tombstone columns - see Customer.MergedIntoCustomerId's own remarks for
        // why a merged row is marked rather than deleted. Self-referencing, cascade on delete: the only
        // way a customers row is ever removed at all is the whole-tenant erasure cascade from `tenants`
        // (`22-30`/personal-data.md's own `customers` row), and when that happens every row under the
        // tenant should go together regardless of which side of a merge it was on.
        builder.Property(c => c.MergedIntoCustomerId)
            .HasColumnName("merged_into_customer_id")
            .HasConversion(IdConverters.NullableCustomer);
        builder.Property(c => c.MergedAt).HasColumnName("merged_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId);
        builder.HasOne<Customer>().WithMany()
            .HasForeignKey(c => c.MergedIntoCustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        // `23-59`/`adr/0147`: narrowed from a plain unique index to a *partial* one, active only for
        // Source = 'Booking' - the identity rule this index's own original remarks describe
        // ("the same person booking at two shops is two cards") still holds exactly as before within
        // that source, but a chat-sourced row must be allowed to share a phone with a booking-sourced
        // one (`adr/0147`'s own "a phone that matches an existing customer does not merge" - a
        // duplicate is the point, not a bug this index should still be preventing). `BookingStore`'s
        // own `ON CONFLICT (tenant_id, phone)` clause carries the identical `WHERE source = 'Booking'`
        // predicate, because Postgres only accepts a partial index as an upsert's conflict target when
        // the statement's own predicate matches it exactly.
        builder.HasIndex(c => new { c.TenantId, c.Phone })
            .IsUnique()
            .HasFilter("source = 'Booking'")
            .HasDatabaseName("ux_customers_tenant_phone");

        // `23-59`/`adr/0147`: the chat-sourced counterpart - one row per (tenant, source contact), so
        // redelivering the identical `ContactCollected` event twice upserts the same row rather than
        // creating a second one. Partial (only when `source_contact_id` is not null) for the same
        // reason `ux_customers_tenant_phone` above is now partial: a `Booking`-sourced row never
        // populates this column, so there is nothing for this index to arbitrate for it.
        builder.HasIndex(c => new { c.TenantId, c.SourceContactId })
            .IsUnique()
            .HasFilter("source_contact_id IS NOT NULL")
            .HasDatabaseName("ux_customers_tenant_source_contact");
    }
}
