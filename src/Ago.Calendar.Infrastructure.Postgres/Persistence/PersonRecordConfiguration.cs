using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// `adr/0184`: <c>person_records</c> - what replaced <c>customers</c>. Keyed by the opaque person id
/// (a bare <c>uuid</c>, no strongly-typed converter - <c>Event.PersonId</c>'s own remarks on why the
/// calendar must not wrap an id it does not own), holding only the write-gating facts rule 8 keeps
/// local: the phone booked with, its two verification marks, the no-show count, first/last seen.
/// No display name, no notes, no source, no merge tombstone - those columns are gone with the copy.
/// </summary>
internal sealed class PersonRecordConfiguration : IEntityTypeConfiguration<PersonRecord>
{
    public void Configure(EntityTypeBuilder<PersonRecord> builder)
    {
        builder.ToTable("person_records");
        builder.HasKey(p => p.PersonId);
        builder.Property(p => p.PersonId).HasColumnName("person_id").ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);

        builder.Property(p => p.Phone)
            .HasColumnName("phone")
            .HasMaxLength(16)
            .HasConversion(IdConverters.Phone)
            .IsRequired();

        builder.Property(p => p.PhoneVerifiedAt).HasColumnName("phone_verified_at").HasColumnType("timestamptz");
        builder.Property(p => p.PhoneConfirmedByOperatorAt)
            .HasColumnName("operator_confirmed_phone_at")
            .HasColumnType("timestamptz");
        builder.Property(p => p.NoShowCount).HasColumnName("no_show_count");
        builder.Property(p => p.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType("timestamptz");
        builder.Property(p => p.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");

        // Cascade from the tenant: the whole-tenant erasure (`22-30`) is the one path that removes a
        // person record today, and every record under the tenant goes with it. A per-person erasure
        // (`adr/0184`: delete one Person in chat, cascade this record + its events by id) is a later
        // item, not a cascade this configuration can express.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);

        // No UNIQUE index on (tenant_id, phone), and that is the decision, not an omission: two people
        // sharing a number are two rows (`adr/0147`'s "a phone is a hint, not proof", kept by
        // `adr/0184`). The plain index serves the one phone-keyed read that survives -
        // IPersonRecordRepository.FindPhoneVerifiedAtAsync's "was this number ever verified here" - and
        // the (tenant_id, last_seen_at) one serves the contacts report's newest-first listing.
        builder.HasIndex(p => new { p.TenantId, p.Phone }).HasDatabaseName("ix_person_records_tenant_phone");
        builder.HasIndex(p => new { p.TenantId, p.LastSeenAt }).HasDatabaseName("ix_person_records_tenant_last_seen");
    }
}
