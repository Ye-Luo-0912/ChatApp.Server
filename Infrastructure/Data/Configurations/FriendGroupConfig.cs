using Core.Models.Friend;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class FriendGroupConfig:IEntityTypeConfiguration<FriendGroup>
{
    public void Configure(EntityTypeBuilder<FriendGroup> builder)
    {
        builder.ToTable("T_FriendGroup");
        builder.HasKey(k => k.GroupId);
        builder.HasIndex(g => new { g.UserId, g.GroupName }).IsUnique();
        
        builder.HasNoForeignKeyConstraints();

        builder.HasOne(f => f.Owner)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .IsRequired(false)
            .HasConstraintName(null);
    }
}
