using Core.Models.Friend;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Models.Config;

public class FriendRequestConfig:IEntityTypeConfiguration<FriendRequest>
{
    public void Configure(EntityTypeBuilder<FriendRequest> builder)
    {
        builder.ToTable("T_FriendRequests");
        builder.HasKey(k => k.RequestId);
        builder.HasIndex(r => new { r.RequesterId, r.TargetUserId }).IsUnique();
        builder.HasIndex(r => r.TargetUserId).HasDatabaseName("IX_FriendRequest_TargetUser");
        
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