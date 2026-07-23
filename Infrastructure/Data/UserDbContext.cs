using System.Text.RegularExpressions;
using Core.Models.Email;
using Core.Models.Export;
using Core.Models.Friend;
using Core.Models.Identity;
using Core.Models.Moderation;
using Core.Models.Notifications;
using Core.Models.Security;
using ChatApp.Realtime.Integration.Outbox;
using Infrastructure.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data
{
    public class UserDbContext(DbContextOptions<UserDbContext> options) : Microsoft.EntityFrameworkCore.DbContext(options)
    {
        public DbSet<ApplicationUser> Users { get; set; }
        public DbSet<ApplicationRoles> Roles { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }

        public DbSet<UserFriendEntry> Friendships { get; set; }
        public DbSet<FriendRequest> FriendRequests { get; set; }
        public DbSet<BlockRecord> BlockRecords { get; set; }
        public DbSet<FriendGroup> FriendGroups { get; set; }
        public DbSet<RealtimeIntegrationOutboxItem> RealtimeOutbox { get; set; }
        public DbSet<EmailOutboxItem> EmailOutbox { get; set; }
        public DbSet<NotificationOutboxItem> NotificationOutbox { get; set; }
        public DbSet<SecurityEvent> SecurityEvents { get; set; }
        public DbSet<AdminAuditLog> AdminAuditLogs { get; set; }
        public DbSet<InAppNotification> InAppNotifications { get; set; }
        public DbSet<UserReport> UserReports { get; set; }
        public DbSet<TrustedDevice> TrustedDevices { get; set; }
        public DbSet<DataExportJob> DataExportJobs { get; set; }
        public DbSet<AttachmentBlobDeleteJob> AttachmentBlobDeleteJobs { get; set; }
        public DbSet<AccountCleanupSaga> AccountCleanupSagas { get; set; }
        public DbSet<AccountCleanupInboxEntry> AccountCleanupInbox { get; set; }
        public DbSet<AccountCleanupDeadLetter> AccountCleanupDeadLetters { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.ApplyConfiguration(new ApplicationUserConfig());

            builder.Entity<ApplicationRoles>(entity =>
            {
                entity.ToTable("AspNetRoles");
                entity.Property(r => r.Id).ValueGeneratedNever();
                entity.Property(r => r.Name).HasMaxLength(256);
                entity.Property(r => r.NormalizedName).HasMaxLength(256);
                entity.HasIndex(r => r.NormalizedName).IsUnique().HasDatabaseName("RoleNameIndex");
            });

            builder.Entity<UserRole>()
                .ToTable("AspNetUserRoles")
                .HasKey(ur => new { ur.UserId, ur.RoleId });

            builder.Entity<UserRole>()
                .HasOne(ur => ur.User)
                .WithMany()
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<UserRole>()
                .HasOne(ur => ur.Role)
                .WithMany()
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SecurityEvent>(e =>
            {
                e.ToTable("T_SecurityEvent");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.DeviceId).HasMaxLength(128);
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.Property(x => x.Location).HasMaxLength(256);
                e.Property(x => x.Detail).HasMaxLength(1024);
                e.Property(x => x.ActorUserId).HasMaxLength(64);
                e.HasIndex(x => new { x.UserId, x.Id }).HasDatabaseName("IX_SecurityEvent_UserId_Id");
            });

            builder.Entity<AdminAuditLog>(e =>
            {
                e.ToTable("T_AdminAuditLog");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.Action).HasMaxLength(64).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(512);
                e.Property(x => x.Detail).HasMaxLength(1024);
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.HasIndex(x => new { x.AdminUserId, x.CreatedAt }).HasDatabaseName("IX_AdminAuditLog_Admin_Created");
                e.HasIndex(x => x.TargetUserId).HasDatabaseName("IX_AdminAuditLog_Target");
            });

            builder.Entity<InAppNotification>(e =>
            {
                e.ToTable("T_InAppNotification");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.Type).HasMaxLength(64).IsRequired();
                e.Property(x => x.Title).HasMaxLength(200).IsRequired();
                e.Property(x => x.Body).HasMaxLength(2000).IsRequired();
                e.HasIndex(x => new { x.UserId, x.Id }).HasDatabaseName("IX_InAppNotification_UserId_Id");
                e.HasIndex(x => x.SourceOutboxId)
                    .IsUnique()
                    .HasFilter("\"SourceOutboxId\" IS NOT NULL")
                    .HasDatabaseName("IX_InAppNotification_SourceOutboxId");
                e.HasIndex(x => x.UserId)
                    .HasFilter("\"IsRead\" = FALSE")
                    .HasDatabaseName("IX_InAppNotification_UserId_Unread");
            });

            builder.Entity<UserReport>(e =>
            {
                e.ToTable("T_UserReport");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.TargetType).HasConversion<byte>();
                e.Property(x => x.Status).HasConversion<byte>();
                e.Property(x => x.Reason).HasMaxLength(200).IsRequired();
                e.Property(x => x.Detail).HasMaxLength(2000);
                e.Property(x => x.TargetMessageId).HasMaxLength(128);
                e.Property(x => x.EvidenceSnapshot).HasMaxLength(4000);
                e.Property(x => x.AppealNote).HasMaxLength(2000);
                e.HasIndex(x => new { x.Status, x.Id }).HasDatabaseName("IX_UserReport_Status_Id");
                e.HasIndex(x => x.TargetUserId).HasDatabaseName("IX_UserReport_TargetUser");
                e.HasIndex(x => new { x.ReporterId, x.TargetUserId, x.TargetMessageId, x.CreatedAt })
                    .HasDatabaseName("IX_UserReport_Reporter_Target_Created");
            });

            builder.Entity<TrustedDevice>(e =>
            {
                e.ToTable("T_TrustedDevice");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.DeviceIdHint).HasMaxLength(128);
                e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
                e.Property(x => x.Label).HasMaxLength(128);
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("IX_TrustedDevice_TokenHash");
                e.HasIndex(x => new { x.UserId, x.ExpiresAt }).HasDatabaseName("IX_TrustedDevice_User_Expires");
            });

            builder.Entity<DataExportJob>(e =>
            {
                e.ToTable("T_DataExportJob");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasMaxLength(32);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.ObjectKey).HasMaxLength(512);
                e.Property(x => x.Error).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.LeaseUntil, x.CreatedAt })
                    .HasDatabaseName("IX_DataExportJob_Claim");
                e.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt })
                    .HasDatabaseName("IX_DataExportJob_User_Status");
                // 每用户至多一个未消费的活跃导出（Pending/Processing/Ready）
                e.HasIndex(x => x.UserId)
                    .IsUnique()
                    .HasFilter(
                        "\"ConsumedAt\" IS NULL AND \"Status\" IN ('Pending', 'Processing', 'Ready')")
                    .HasDatabaseName("UX_DataExportJob_OneActive");
            });

            builder.Entity<AttachmentBlobDeleteJob>(e =>
            {
                e.ToTable("T_AttachmentBlobDeleteJob");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.AttachmentId).HasMaxLength(64);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_Due");
                e.HasIndex(x => x.ObjectKey)
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_ObjectKey");
                e.HasIndex(x => x.UserId)
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_UserId");
            });

            builder.Entity<AccountCleanupSaga>(e =>
            {
                e.ToTable("T_AccountCleanupSaga");
                e.HasKey(x => x.UserId);
                e.Property(x => x.UserId).ValueGeneratedNever();
                e.Property(x => x.EventId).HasMaxLength(64).IsRequired();
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.ReplayCount).HasDefaultValue(0);
                e.HasIndex(x => new { x.Status, x.CreatedAt })
                    .HasDatabaseName("IX_AccountCleanupSaga_Status_Created");
            });

            builder.Entity<AccountCleanupInboxEntry>(e =>
            {
                e.ToTable("T_AccountCleanupInbox");
                e.HasKey(x => x.EventId);
                e.Property(x => x.EventId).HasMaxLength(128);
                e.Property(x => x.Outcome).HasMaxLength(64).IsRequired();
                e.HasIndex(x => new { x.UserId, x.ProcessedAt })
                    .HasDatabaseName("IX_AccountCleanupInbox_User_Processed");
            });

            builder.Entity<AccountCleanupDeadLetter>(e =>
            {
                e.ToTable("T_AccountCleanupDeadLetter");
                e.HasKey(x => x.Id);
                e.Property(x => x.EventId).HasMaxLength(128).IsRequired();
                e.Property(x => x.ReasonCode).HasMaxLength(64).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
                e.Property(x => x.PayloadJson).HasMaxLength(4000);
                e.HasIndex(x => new { x.EventId, x.ReasonCode })
                    .IsUnique()
                    .HasDatabaseName("UX_AccountCleanupDeadLetter_Event_Reason");
                e.HasIndex(x => x.CreatedAt)
                    .HasDatabaseName("IX_AccountCleanupDeadLetter_Created");
            });

            builder.Ignore<Capture>();
            builder.ApplyConfiguration(new FriendshipConfig());
            builder.ApplyConfiguration(new FriendRequestConfig());
            builder.ApplyConfiguration(new BlockRecordConfig());
            builder.ApplyConfiguration(new FriendGroupConfig());
            builder.ApplyConfiguration(new EmailOutboxItemConfig());
            builder.ApplyConfiguration(new NotificationOutboxItemConfig());
            builder.AddChatAppRealtimeOutbox();
        }
    }
}
