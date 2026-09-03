using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class RoleAssignmentProjectionConfiguration : IEntityTypeConfiguration<RoleAssignmentProjectionRecord>
{
    public void Configure(EntityTypeBuilder<RoleAssignmentProjectionRecord> builder)
    {
        builder.ToTable("role_assignment_projections");

        // Composite key on the pair the row is a fact about - RoleAssignmentProjectionRecord's own
        // remarks explain why this is a join fact with no surrogate id, the same shape the
        // `operator_roles` table this migration drops used to draw for itself.
        builder.HasKey(r => new { r.OperatorId, r.TenantId });
        builder.Property(r => r.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator);
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);

        builder.Property(r => r.ExternalSubjectId).HasColumnName("external_subject_id").IsRequired();

        // A plain `text[]`, not the value-converter route `Role.Permissions` used to take - see
        // PermissionChecker's own remarks on why that converter existed and why this table does not
        // need one: nothing here re-hydrates a Domain `Permission` from a row, so there is nothing to
        // convert into.
        builder.Property(r => r.Permissions).HasColumnName("permissions").IsRequired();

        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();

        // No separate index on OperatorId alone: it is the composite primary key's leading column, and
        // a Postgres btree already serves a lookup on a leftmost prefix - `OperatorIdentityClaimsTransformation`'s
        // own reverse lookup (find every tenant for one operator id) rides on the primary key for free.
    }
}
