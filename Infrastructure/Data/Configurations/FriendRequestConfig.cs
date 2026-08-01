using Core.Models.Friend;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class FriendRequestConfig:IEntityTypeConfiguration<FriendRequest>
{
    public void Configure(EntityTypeBuilder<FriendRequest> builder)
    {
        builder.ToTable("T_FriendRequests");
        builder.HasKey(k => k.RequestId);
        builder.HasIndex(r => new { r.RequesterId, r.TargetUserId }).IsUnique();
        builder.HasIndex(r => r.TargetUserId).HasDatabaseName("IX_FriendRequest_TargetUser");
        builder.HasIndex(r => new { r.TargetUserId, r.Status });
        builder.HasIndex(r => new { r.RequesterId, r.Status });

        builder.Property(r => r.Message).HasMaxLength(FriendshipInputLimits.FriendRequestMessageMaxLength);
        
        builder.HasNoForeignKeyConstraints();

        builder.HasOne(r => r.Requester)
            .WithMany()
            .HasForeignKey(r => r.RequesterId)
            .IsRequired(false)
            .HasConstraintName(null);
            

        builder.HasOne(r => r.TargetUser)
            .WithMany()
            .HasForeignKey(r => r.TargetUserId)
            .IsRequired(false)
            .HasConstraintName(null);
    }
}
