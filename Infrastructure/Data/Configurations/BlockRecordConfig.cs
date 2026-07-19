using Core.Models.Friend;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class BlockRecordConfig:IEntityTypeConfiguration<BlockRecord>
{
    public void Configure(EntityTypeBuilder<BlockRecord> builder)
    {
        builder.ToTable("T_BlockRecords");
        builder.HasKey(k => k.BlockId);
        builder.HasIndex(b => new { b.BlockerId, b.BlockedUserId }).IsUnique();

        builder.HasNoForeignKeyConstraints();

        builder.HasOne(b => b.Blocker)
            .WithMany()
            .HasForeignKey(f => f.BlockerId)
            .IsRequired(false)
            .HasConstraintName(null);       
        
        builder.HasOne(b => b.BlockedUser)
            .WithMany()
            .HasForeignKey(f => f.BlockedUserId)
            .IsRequired(false)
            .HasConstraintName(null);
    }
}
