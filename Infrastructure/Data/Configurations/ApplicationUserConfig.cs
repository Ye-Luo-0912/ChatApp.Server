using Core.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class ApplicationUserConfig : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("AspNetUsers");
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.UserName).HasMaxLength(256);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(256);
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.NormalizedEmail).HasMaxLength(256);
        builder.Property(u => u.PendingEmail).HasMaxLength(256);
        builder.Property(u => u.NormalizedPendingEmail).HasMaxLength(256);
        builder.Property(u => u.SecurityVersion)
            .HasDefaultValue(1L)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(u => u.PasswordHashVersion)
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(u => u.AccountState)
            .HasConversion<short>()
            .HasDefaultValue(Core.Models.Identity.AccountState.Active)
            .IsRequired();
        builder.Property(u => u.FriendRequestPolicy)
            .HasConversion<byte>()
            .HasDefaultValue(FriendRequestPolicy.RequireVerification)
            .IsRequired();
        builder.Property(u => u.NotifyFriendRequests)
            .HasDefaultValue(true)
            .IsRequired();
        builder.Property(u => u.PhoneNumber).HasMaxLength(32);
        builder.Property(u => u.NormalizedPhoneNumber).HasMaxLength(16);
        builder.Property(u => u.PendingPhoneNumber).HasMaxLength(32);
        builder.Property(u => u.NormalizedPendingPhoneNumber).HasMaxLength(16);
        builder.HasIndex(u => u.NormalizedPhoneNumber)
            .IsUnique()
            .HasFilter("\"NormalizedPhoneNumber\" IS NOT NULL")
            .HasDatabaseName("UX_AspNetUsers_NormalizedPhoneNumber");
        builder.HasIndex(u => u.NormalizedPendingPhoneNumber)
            .HasDatabaseName("IX_AspNetUsers_NormalizedPendingPhoneNumber");

        builder.HasIndex(u => u.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("EmailIndex");

        builder.HasIndex(u => u.NormalizedPendingEmail)
            .HasDatabaseName("IX_AspNetUsers_NormalizedPendingEmail");

        builder.HasIndex(u => u.NormalizedUserName)
            .IsUnique()
            .HasDatabaseName("UserNameIndex");

        builder.Property(u => u.AvatarUrl)
            .HasMaxLength(2048);
        builder.Property(u => u.AvatarVersion)
            .HasDefaultValue(1L)
            .IsRequired()
            .IsConcurrencyToken();

        builder.Property(u => u.Signature)
            .HasMaxLength(500);

        builder.Property(u => u.Region)
            .HasMaxLength(200);

        builder.Property(u => u.AllowBeSearched)
            .HasDefaultValue(true);

        builder.Property(u => u.NotifySecurityEmail).HasDefaultValue(true);
        builder.Property(u => u.TotpSecret).HasMaxLength(512);
        builder.Property(u => u.PendingTotpSecret).HasMaxLength(512);
        builder.Property(u => u.RecoveryCodesHashJson).HasColumnType("text");
        builder.Property(u => u.PendingRecoveryCodesHashJson).HasColumnType("text");
        builder.Property(u => u.MustChangePassword).HasDefaultValue(false);
        builder.Property(u => u.DeletionLeaseOwner).HasMaxLength(64);
        builder.Property(u => u.DeletionLeaseToken).HasMaxLength(64);
        builder.Property(u => u.DeletionLastError).HasMaxLength(500);
        builder.HasIndex(u => new { u.DeletionScheduledAt, u.DeletionNextAttemptAt })
            .HasDatabaseName("IX_AspNetUsers_DeletionDue");

        builder.HasIndex(u => u.DeletionScheduledAt)
            .HasDatabaseName("IX_AspNetUsers_DeletionScheduledAt")
            .HasFilter("\"DeletionScheduledAt\" IS NOT NULL");
    }
}
