using Core.Models.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class ModerationSessionRevocationOutboxItemConfig
    : IEntityTypeConfiguration<ModerationSessionRevocationOutboxItem>
{
    public void Configure(EntityTypeBuilder<ModerationSessionRevocationOutboxItem> builder)
    {
        builder.ToTable("T_ModerationSessionRevocationOutbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Status).HasConversion<byte>();
        builder.Property(x => x.LastError).HasMaxLength(1000);
        builder.Property(x => x.LeaseOwner).HasMaxLength(128);
        builder.Property(x => x.LeaseToken).HasMaxLength(64);

        builder.HasIndex(x => x.SourceReportId)
            .IsUnique()
            .HasDatabaseName("UX_ModerationSessionRevocationOutbox_SourceReportId");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("IX_ModerationSessionRevocationOutbox_Status_NextAttemptAt");
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
            .HasDatabaseName("IX_ModerationSessionRevocationOutbox_Status_LeaseExpiresAt");
    }
}
