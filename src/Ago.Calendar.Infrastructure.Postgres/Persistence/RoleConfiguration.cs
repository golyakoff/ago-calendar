using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <summary>
    /// EF compares a mutable reference-typed property by reference unless told otherwise, which for
    /// a collection means "never changed" - the classic silent-no-op this comparer exists to
    /// prevent. Structural equality plus a real hash, exactly as EF's own documentation prescribes
    /// for a converted collection property.
    /// </summary>
    private static readonly ValueComparer<List<Permission>> PermissionsComparer = new(
        (left, right) => left != null && right != null && left.SequenceEqual(right),
        permissions => permissions.Aggregate(0, (hash, permission) => HashCode.Combine(hash, permission.GetHashCode())),
        permissions => permissions.ToList());

    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").HasConversion(IdConverters.Role).ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // A Postgres text[] column, exactly as adr/0016 and ago-chat's own roles table do it - not a
        // join table. Permissions are read as a whole set on every authorization check and never
        // queried individually, so normalising them would buy a join for a cardinality of seven.
        builder.Property<List<Permission>>("_permissions")
            .HasColumnName("permissions")
            .HasConversion(
                permissions => permissions.Select(permission => permission.Value).ToArray(),
                values => values.Select(value => new Permission(value)).ToList(),
                PermissionsComparer)
            .IsRequired();
        builder.Ignore(r => r.Permissions);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId);

        // One role name per tenant. Not global: two tenants both having an "Operator" role is the
        // normal case, and a global unique index would make the second tenant's provisioning fail.
        builder.HasIndex(r => new { r.TenantId, r.Name })
            .IsUnique()
            .HasDatabaseName("ux_roles_tenant_name");
    }
}
