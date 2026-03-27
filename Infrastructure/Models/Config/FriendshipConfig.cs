using Core.Models.Friend;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Models.Config;

public class FriendshipConfig:IEntityTypeConfiguration<UserFriendEntry>
{
    public void Configure(EntityTypeBuilder<UserFriendEntry> builder)
    {
        builder.ToTable("T_UserFriendEntry");
        // 主键配置
        builder.HasKey(f => f.FriendshipId);

        // 复合唯一约束（替代备用键）
        builder.HasIndex(f => new { f.UserId, f.FriendId })
            .IsUnique()
            .HasDatabaseName("IX_User_Friend_Unique");
        
        builder.HasNoForeignKeyConstraints();

        // 用户关系配置
        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .IsRequired(false)
            .HasConstraintName(null);

        // 好友关系配置
        builder.HasOne(f => f.Friend)
            .WithMany()
            .HasForeignKey(f => f.FriendId)
            .IsRequired(false)
            .HasConstraintName(null);

        // 分组关系配置
        builder.HasOne(f => f.Group)
            .WithMany()
            .HasForeignKey(f => f.GroupId)
            .IsRequired(false)
            .HasConstraintName(null);
        
        builder.HasQueryFilter(f => !f.IsDeleted); // 全局软删除过滤
    }
}