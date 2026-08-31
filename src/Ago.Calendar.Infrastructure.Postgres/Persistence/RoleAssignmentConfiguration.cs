using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("operator_roles");

        // The pair is the key. A surrogate id on a pure join row would add a column, an index and a
        // sequence, and would let the same pair be inserted twice - the composite key is both the
        // identity and the uniqueness rule in one.
        builder.HasKey(a => new { a.OperatorId, a.RoleId });
        builder.Property(a => a.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator);
        builder.Property(a => a.RoleId).HasColumnName("role_id").HasConversion(IdConverters.Role);

        // `20-12`: RoleAssignment.GrantsCustomerRead's own remarks explain why this is safe to store
        // as a snapshot rather than recomputed by joining back to roles.permissions on every check.
        builder.Property(a => a.GrantsCustomerRead).HasColumnName("grants_customer_read").IsRequired();

        builder.HasOne<Role>().WithMany().HasForeignKey(a => a.RoleId);
    }
}
