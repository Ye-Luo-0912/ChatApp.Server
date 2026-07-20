using Core.Models.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class NotificationOutboxItemConfig : IEntityTypeConfiguration<NotificationOutboxItem>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxItem> builder)
    {
        builder.ToTable("T_NotificationOutbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Type).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128);
        builder.Property(x => x.LastError).HasMaxLength(1000);
        builder.Property(x => x.LockOwner).HasMaxLength(64);
        builder.Property(x => x.Status).HasConversion<byte>();

        builder.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("IX_NotificationOutbox_Status_NextAttemptAt");
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN (0, 1, 3)")
            .HasDatabaseName("IX_NotificationOutbox_IdempotencyKey_Active");
        builder.HasIndex(x => new { x.Status, x.LockedAt })
            .HasDatabaseName("IX_NotificationOutbox_Status_LockedAt");
    }
}
