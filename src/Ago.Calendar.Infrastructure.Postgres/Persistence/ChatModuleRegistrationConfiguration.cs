using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class ChatModuleRegistrationConfiguration : IEntityTypeConfiguration<ChatModuleRegistration>
{
    public void Configure(EntityTypeBuilder<ChatModuleRegistration> builder)
    {
        builder.ToTable("chat_module_registrations");

        // TenantId is the primary key, not a surrogate id - one row per tenant by construction
        // (Domain.ChatModuleRegistration.Register takes no id of its own to generate), the same call
        // Ago.Faq.Infrastructure.Postgres.Persistence.ModuleSiteRegistrationConfiguration's own
        // remarks make for its sibling.
        builder.HasKey(r => r.TenantId);
        builder.Property(r => r.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(IdConverters.Tenant)
            .ValueGeneratedNever();

        // A real foreign key, not merely a matching Guid - this row is meaningless without a tenant
        // to belong to, and a registration outliving its tenant would be an orphaned secret nobody
        // can ever reach through the API to rotate or revoke.
        builder.HasOne<Tenant>().WithOne().HasForeignKey<ChatModuleRegistration>(r => r.TenantId);

        builder.Property(r => r.Credential)
            .HasColumnName("credential")
            .HasMaxLength(ChatModuleCredential.MaxLength)
            .HasConversion(c => c.Value, v => new ChatModuleCredential(v))
            .IsRequired();

        // `22-11`: nullable, unlike Credential above - most rows are never rotated, and a row that
        // has never been rotated (or whose grace window has already elapsed) has no previous secret
        // at all, not an empty one. See ChatModuleRegistration.Rotate's own remarks for when this is
        // populated and ActiveCredentials for when it is read.
        builder.Property(r => r.PreviousCredential)
            .HasColumnName("previous_credential")
            .HasMaxLength(ChatModuleCredential.MaxLength)
            .HasConversion(
                c => c == null ? null : c.Value.Value,
                v => v == null ? null : new ChatModuleCredential(v));

        builder.Property(r => r.PreviousCredentialExpiresAt)
            .HasColumnName("previous_credential_expires_at")
            .HasColumnType("timestamptz");

        builder.Property(r => r.RegisteredAt).HasColumnName("registered_at").HasColumnType("timestamptz");
    }
}
