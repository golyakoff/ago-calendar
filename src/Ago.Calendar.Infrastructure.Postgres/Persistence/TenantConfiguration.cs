using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    /// <summary>
    /// The same reason <c>RoleConfiguration</c> carries one: EF compares a mutable reference-typed
    /// property by reference unless told otherwise, so an edited origin list would be seen as
    /// "never changed" and the update would silently do nothing.
    /// </summary>
    private static readonly ValueComparer<List<string>> OriginsComparer = new(
        (left, right) => left != null && right != null && left.SequenceEqual(right, StringComparer.Ordinal),
        origins => origins.Aggregate(0, (hash, origin) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(origin))),
        origins => origins.ToList());

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(IdConverters.Tenant).ValueGeneratedNever();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // `20-06`: the public embed surface. Unique, because the key is how a script tag names a
        // tenant and two tenants answering to one key is an ambiguity no later check could resolve.
        builder.Property(t => t.PublicKey)
            .HasColumnName("public_key")
            .HasMaxLength(TenantPublicKey.MaxLength)
            .HasConversion(key => key.Value, value => new TenantPublicKey(value))
            .IsRequired();
        builder.HasIndex(t => t.PublicKey).IsUnique().HasDatabaseName("ux_tenants_public_key");

        // text[], the same shape ago-chat's own allowed_origins uses, and the reason the layer-1
        // lookup can be a single indexed `@origin = ANY(allowed_origins)` rather than a join through
        // a child table. A GIN index makes that predicate sargable; without one, layer 1 is a
        // sequential scan of every tenant on every preflight.
        builder.Property<List<string>>("_allowedOrigins")
            .HasColumnName("allowed_origins")
            .HasColumnType("text[]")
            .HasConversion(
                origins => origins.ToArray(),
                values => values.ToList(),
                OriginsComparer)
            .IsRequired();
        builder.Ignore(t => t.AllowedOrigins);
        builder.HasIndex("_allowedOrigins")
            .HasDatabaseName("ix_tenants_allowed_origins")
            .HasMethod("gin");

        // timestamptz, from IClock, never a database default - date-and-time.md rule 1 and rule 3
        // together: the value is UTC and it comes from the application, so a test can control it.
        // Npgsql maps DateTimeOffset to timestamptz by default; stated here because the mapping is
        // the guarantee, not a convention anyone should have to look up.
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

        // `22-17`: the provenance marker - see Tenant.AutoProvisioned's own remarks.
        builder.Property(t => t.AutoProvisioned).HasColumnName("auto_provisioned").HasDefaultValue(false);
    }
}
