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

        // `23-35`. Free text, validated in the domain constructor rather than by a CHECK constraint -
        // the same division data-model.md already draws for `sites.widget_notice_text`: this is not a
        // closed set SQL can enumerate the way `widget_position`'s CHECK does.
        builder.Property(s => s.Description).HasColumnName("description").HasMaxLength(1000);

        // A complex property, not two independent nullable columns exposed on the aggregate: the two
        // columns must always be read and written together (an amount with no currency, or a currency
        // with no amount, is not a state this product wants a caller able to construct), which is the
        // identical "two columns that must agree are two columns that can disagree" reasoning
        // data-model.md's own `sites.demo_expires_at` remarks give for having no second `is_demo`
        // column. EF Core's nullable complex-property support (added EF9, carried into the EF10 this
        // project pins) is what lets `Money?` be null-as-a-whole across both columns rather than
        // needing a sentinel value or a third "has price" boolean.
        builder.ComplexProperty(s => s.Price, price =>
        {
            price.IsRequired(false);
            price.Property(m => m.MinorUnits).HasColumnName("price_minor_units");
            price.Property(m => m.CurrencyCode).HasColumnName("price_currency_code").HasMaxLength(3);
        });

        // `23-35`. Meaningless while Price is null - Service's own constructor already normalises that
        // case to false, so this column is never true with a null price sitting beside it.
        builder.Property(s => s.PriceIsFrom).HasColumnName("price_is_from").IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
    }
}
