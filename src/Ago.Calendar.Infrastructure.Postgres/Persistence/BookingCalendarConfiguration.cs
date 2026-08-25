using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class BookingCalendarConfiguration : IEntityTypeConfiguration<BookingCalendar>
{
    public void Configure(EntityTypeBuilder<BookingCalendar> builder)
    {
        // The table keeps the product's own word even though the CLR type could not - see
        // BookingCalendar's own remarks on CS0118. A rename forced by a namespace collision has no
        // business leaking into the schema.
        builder.ToTable("calendars");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasConversion(IdConverters.Calendar).ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // An IANA zone id as text - never an offset, and never Postgres's own timezone type. The
        // column is an input to a conversion the application performs; the database never resolves
        // it, so it needs no more than the name.
        builder.Property(c => c.TimeZone)
            .HasColumnName("time_zone")
            .HasMaxLength(64)
            .HasConversion(IdConverters.TimeZone)
            .IsRequired();

        builder.Property(c => c.BufferMinutes).HasColumnName("buffer_minutes");
        builder.Property(c => c.IsPublished).HasColumnName("is_published");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId);

        // The publish switch is a filter on a per-tenant listing, so the index carries the tenant and
        // is partial on the flag - data-model.md's own preference for partial indexes on queue-like
        // and flag-like predicates, applied to IBookingCalendarRepository.ListPublishedAsync.
        builder.HasIndex(c => c.TenantId)
            .HasDatabaseName("ix_calendars_published")
            .HasFilter("is_published");
    }
}
