using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

internal sealed class PendingPhoneVerificationConfiguration : IEntityTypeConfiguration<PendingPhoneVerification>
{
    public void Configure(EntityTypeBuilder<PendingPhoneVerification> builder)
    {
        builder.ToTable("pending_phone_verifications");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id").HasConversion(IdConverters.PendingPhoneVerification).ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasConversion(IdConverters.Tenant);

        // A plain string column, not IdConverters.Phone: this row's own Phone is compared against a
        // caller-supplied raw phone number by PendingPhoneVerification.IsProofValid, and the aggregate
        // itself already normalises through PhoneNumber before that comparison ever runs (the same
        // "canonical string in, canonical string compared" shape Customer.Phone follows via
        // IdConverters.Phone) - kept as a plain string here only because the aggregate's own
        // constructor stores PhoneNumber.Value directly rather than a PhoneNumber, mirroring `ago-chat`'s
        // own PendingPhoneVerification.Phone column exactly.
        builder.Property(p => p.Phone).HasColumnName("phone").HasMaxLength(20).IsRequired();

        builder.Property(p => p.CodeHash).HasColumnName("code_hash").IsRequired();

        // Stored as the CLR member name, the same convention every enum column in this schema uses.
        builder.Property(p => p.DeliveryMethod).HasColumnName("delivery_method").HasConversion<string>().HasMaxLength(16);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        builder.Property(p => p.ConsumedAt).HasColumnName("consumed_at").HasColumnType("timestamptz");
        builder.Property(p => p.AttemptCount).HasColumnName("attempt_count");
        builder.Property(p => p.MaxAttempts).HasColumnName("max_attempts");

        builder.Property(p => p.ProofTokenHash).HasColumnName("proof_token_hash");
        builder.Property(p => p.ProofExpiresAt).HasColumnName("proof_expires_at").HasColumnType("timestamptz");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId);

        // Both real callers (ConfirmPhoneVerificationHandler, PhoneVerificationAssertionResolver) look
        // up by primary key alone - no extra index beyond the primary key and the FK index EF already
        // creates for the HasOne call above, the identical "no FindLive-shaped query, no extra index"
        // call `ago-chat`'s own PendingPhoneVerificationConfiguration makes.
    }
}
