using Core.Models.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class EmailOutboxItemConfig : IEntityTypeConfiguration<EmailOutboxItem>
{
    public void Configure(EntityTypeBuilder<EmailOutboxItem> builder)
    {
        builder.ToTable("T_EmailOutbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.To)
            .HasColumnName("to_address")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(x => x.Subject)
            .HasMaxLength(998)
            .IsRequired();

        builder.Property(x => x.Body)
            .IsRequired();

        builder.Property(x => x.LastError)
            .HasMaxLength(2048);

        builder.Property(x => x.EmailType)
            .HasMaxLength(64);

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(256);

        builder.Property(x => x.LockOwner)
            .HasMaxLength(128);

        builder.Property(x => x.LeaseToken)
            .HasMaxLength(64);

        builder.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("IX_EmailOutbox_Status_NextAttemptAt");

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN (0, 1, 3)")
            .HasDatabaseName("IX_EmailOutbox_IdempotencyKey_Active");

        builder.HasIndex(x => new { x.Status, x.LockedAt })
            .HasDatabaseName("IX_EmailOutbox_Status_LockedAt");
    }
}
