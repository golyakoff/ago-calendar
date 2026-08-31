using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasConversion(IdConverters.Operator).ValueGeneratedNever();
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);
        builder.Property(o => o.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();

        // adr/0022's shape, adr/0027's copy of it: nullable, because an operator can be provisioned
        // before anyone signs in, and unique when present, because two operator rows resolving to
        // one Keycloak subject would make "who is this token" ambiguous. The same `sub` may also
        // exist in ago-chat's own operators table - a different database this product cannot see.
        builder.Property(o => o.ExternalSubjectId).HasColumnName("external_subject_id").HasMaxLength(255);
        builder.HasIndex(o => o.ExternalSubjectId)
            .IsUnique()
            .HasDatabaseName("ux_operators_external_subject_id")
            .HasFilter("external_subject_id IS NOT NULL");

        // `20-12`: get-only, set once at Operator.Create - see Operator.IsAccountOwner's own remarks
        // for why there is no mutator. EF materialises a get-only auto-property through its own
        // backing field with no extra configuration needed here, the same way `Id`/`TenantId` above
        // already do.
        builder.Property(o => o.IsAccountOwner).HasColumnName("is_account_owner").IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId);

        // Never a settable collection (clean-architecture.md: no public setters) - EF is pointed at
        // the private backing field for both reads and materialization, so an operator loads without
        // going through Grant().
        builder.Ignore(o => o.Roles);
        builder.HasMany<RoleAssignment>("_roles")
            .WithOne()
            .HasForeignKey(a => a.OperatorId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation("_roles").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
