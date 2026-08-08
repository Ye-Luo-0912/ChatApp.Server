using System.Text.RegularExpressions;
using Core.Models.Auth;
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

        public DbSet<RealtimeIntegrationOutboxItem> RealtimeOutbox { get; set; }
        public DbSet<EmailOutboxItem> EmailOutbox { get; set; }
        public DbSet<NotificationOutboxItem> NotificationOutbox { get; set; }
        public DbSet<ModerationSessionRevocationOutboxItem> ModerationSessionRevocationOutbox { get; set; }
        public DbSet<SecurityEvent> SecurityEvents { get; set; }
        public DbSet<LoginAuditOutboxItem> LoginAuditOutbox { get; set; }
        public DbSet<LoginRiskOutboxItem> LoginRiskOutbox { get; set; }
        public DbSet<AdminAuditLog> AdminAuditLogs { get; set; }
        public DbSet<InAppNotification> InAppNotifications { get; set; }
        public DbSet<UserReport> UserReports { get; set; }
        public DbSet<TrustedDevice> TrustedDevices { get; set; }
        public DbSet<DataExportJob> DataExportJobs { get; set; }
        public DbSet<AttachmentBlobDeleteJob> AttachmentBlobDeleteJobs { get; set; }
        public DbSet<AttachmentScanJob> AttachmentScanJobs { get; set; }
        public DbSet<AttachmentScanAudit> AttachmentScanAudits { get; set; }
        public DbSet<AttachmentScanProjection> AttachmentScanProjections { get; set; }
        public DbSet<AttachmentConfirmSaga> AttachmentConfirmSagas { get; set; }
        public DbSet<AvatarFinalizationSaga> AvatarFinalizationSagas { get; set; }
        public DbSet<AccountCleanupSaga> AccountCleanupSagas { get; set; }
        public DbSet<AccountCleanupInboxEntry> AccountCleanupInbox { get; set; }
        public DbSet<AccountCleanupDeadLetter> AccountCleanupDeadLetters { get; set; }
        public DbSet<MfaRecoveryCodeClaimEntity> MfaRecoveryCodeClaims { get; set; }
        public DbSet<SecurityOperationGrant> SecurityOperationGrants { get; set; }
        public DbSet<JobDeadLetterResolution> JobDeadLetterResolutions { get; set; }
        public DbSet<SecuritySessionRevocationOutboxItem> SecuritySessionRevocationOutbox { get; set; }
        public DbSet<UserFriendEntry> Friendships { get; set; }
        public DbSet<FriendRequest> FriendRequests { get; set; }
        public DbSet<FriendGroup> FriendGroups { get; set; }
        public DbSet<BlockRecord> BlockRecords { get; set; }

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
                e.Property(x => x.SessionId).HasMaxLength(128);
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.Property(x => x.Location).HasMaxLength(256);
                e.Property(x => x.Detail).HasMaxLength(1024);
                e.Property(x => x.ActorUserId).HasMaxLength(64);
                e.HasIndex(x => x.SourceLoginAuditOutboxId)
                    .IsUnique()
                    .HasFilter("\"SourceLoginAuditOutboxId\" IS NOT NULL")
                    .HasDatabaseName("UX_SecurityEvent_LoginAuditOutbox");
                e.HasIndex(x => new { x.UserId, x.Id }).HasDatabaseName("IX_SecurityEvent_UserId_Id");
            });

            builder.Entity<LoginAuditOutboxItem>(e =>
            {
                e.ToTable("T_LoginAuditOutbox");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.EventType).HasConversion<short>().IsRequired();
                e.Property(x => x.DeviceId).HasMaxLength(128);
                e.Property(x => x.SessionId).HasMaxLength(128);
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.Property(x => x.Location).HasMaxLength(256);
                e.Property(x => x.Detail).HasMaxLength(1024);
                e.Property(x => x.ActorUserId).HasMaxLength(64);
                e.Property(x => x.Status).HasConversion<byte>().IsRequired();
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_LoginAuditOutbox_Status_NextAttemptAt");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_LoginAuditOutbox_Status_LeaseExpiresAt");
            });

            builder.Entity<LoginRiskOutboxItem>(e =>
            {
                e.ToTable("T_LoginRiskOutbox");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ClientIp).HasMaxLength(64);
                e.Property(x => x.DeviceId).HasMaxLength(128);
                e.Property(x => x.SessionId).HasMaxLength(128);
                e.Property(x => x.RuleVersion).HasDefaultValue(1).IsRequired();
                e.Property(x => x.Status).HasConversion<byte>().IsRequired();
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_LoginRiskOutbox_Status_NextAttemptAt");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_LoginRiskOutbox_Status_LeaseExpiresAt");
            });

            builder.Entity<MfaRecoveryCodeClaimEntity>(e =>
            {
                e.ToTable("T_MfaRecoveryCodeClaim");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ClaimToken).HasMaxLength(96).IsRequired();
                e.Property(x => x.CodeDigest).HasMaxLength(512).IsRequired();
                e.Property(x => x.OriginalCodesJson).HasColumnType("text").IsRequired();
                e.Property(x => x.RemainingCodesJson).HasColumnType("text").IsRequired();
                e.Property(x => x.State).HasConversion<short>().IsRequired();
                e.HasIndex(x => x.ClaimToken)
                    .IsUnique()
                    .HasDatabaseName("UX_MfaRecoveryCodeClaim_Token");
                e.HasIndex(x => new { x.State, x.ExpiresAt })
                    .HasDatabaseName("IX_MfaRecoveryCodeClaim_State_Expires");
                e.HasIndex(x => new { x.UserId, x.State })
                    .HasDatabaseName("IX_MfaRecoveryCodeClaim_User_State");
                e.HasOne<Core.Models.Identity.ApplicationUser>()
                    .WithMany()
                    .HasForeignKey(x => x.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<SecurityOperationGrant>(e =>
            {
                e.ToTable("T_SecurityOperationGrant");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.GrantHash).HasMaxLength(64).IsRequired();
                e.Property(x => x.Purpose).HasMaxLength(64).IsRequired();
                e.Property(x => x.PayloadHash).HasMaxLength(128);
                e.Property(x => x.State)
                    .HasConversion<byte>()
                    .IsRequired()
                    .IsConcurrencyToken();
                e.HasIndex(x => x.GrantHash)
                    .IsUnique()
                    .HasDatabaseName("UX_SecurityOperationGrant_Hash");
                e.HasIndex(x => new { x.UserId, x.State, x.ExpiresAt })
                    .HasDatabaseName("IX_SecurityOperationGrant_User_State_Expires");
                e.HasOne<Core.Models.Identity.ApplicationUser>()
                    .WithMany()
                    .HasForeignKey(x => x.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<JobDeadLetterResolution>(e =>
            {
                e.ToTable("T_JobDeadLetterResolution");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.Queue).HasMaxLength(64).IsRequired();
                e.Property(x => x.JobId).HasMaxLength(128).IsRequired();
                e.Property(x => x.Action).HasMaxLength(32).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(1000);
                e.HasIndex(x => new { x.Queue, x.JobId, x.CreatedAt })
                    .HasDatabaseName("IX_JobDeadLetterResolution_Queue_Job_Created");
            });

            builder.Entity<SecuritySessionRevocationOutboxItem>(e =>
            {
                e.ToTable("T_SecuritySessionRevocationOutbox");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ExceptDeviceId).HasMaxLength(128);
                e.Property(x => x.EventType).HasConversion<short>().IsRequired();
                e.Property(x => x.Status).HasConversion<byte>().IsRequired();
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_SecuritySessionRevocationOutbox_Status_NextAttemptAt");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_SecuritySessionRevocationOutbox_Status_LeaseExpiresAt");
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
                e.Property(x => x.EvidenceSnapshot).HasColumnType("jsonb");
                e.Property(x => x.EvidenceBodyPreview).HasMaxLength(4000);
                e.Property(x => x.EvidenceContentHash).HasMaxLength(128);
                e.Property(x => x.DedupeKey).HasMaxLength(256);
                e.Property(x => x.AppealNote).HasMaxLength(2000);
                e.HasIndex(x => new { x.Status, x.Id }).HasDatabaseName("IX_UserReport_Status_Id");
                e.HasIndex(x => x.TargetUserId).HasDatabaseName("IX_UserReport_TargetUser");
                e.HasIndex(x => new { x.ReporterId, x.TargetUserId, x.TargetMessageId, x.CreatedAt })
                    .HasDatabaseName("IX_UserReport_Reporter_Target_Created");
                e.HasIndex(x => x.DedupeKey)
                    .IsUnique()
                    .HasFilter("\"DedupeKey\" IS NOT NULL")
                    .HasDatabaseName("UX_UserReport_DedupeKey");
            });

            builder.Entity<TrustedDevice>(e =>
            {
                e.ToTable("T_TrustedDevice");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.SecurityVersion).HasDefaultValue(1L).IsRequired();
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
                // P0-5.2：LeaseToken fencing 列，与 LeaseOwner 同时匹配以保证只有当前持有者能落终态。
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt })
                    .HasDatabaseName("IX_DataExportJob_Due");
                e.HasIndex(x => new { x.Status, x.LeaseUntil, x.CreatedAt })
                    .HasDatabaseName("IX_DataExportJob_Claim");
                e.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt })
                    .HasDatabaseName("IX_DataExportJob_User_Status");
                // 每用户至多一个未消费的活跃导出（Pending/Processing/Ready）
                e.HasIndex(x => x.UserId)
                    .IsUnique()
                    .HasFilter(
                        "\"ConsumedAt\" IS NULL AND \"Status\" IN ('Pending', 'Processing', 'CancelRequested', 'Ready')")
                    .HasDatabaseName("UX_DataExportJob_OneActive");
            });

            builder.Entity<AttachmentBlobDeleteJob>(e =>
            {
                e.ToTable("T_AttachmentBlobDeleteJob");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.StorageKind)
                    .HasMaxLength(32)
                    .HasDefaultValue(AttachmentBlobDeleteStorageKind.Attachment)
                    .IsRequired();
                e.Property(x => x.AttachmentId).HasMaxLength(64);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_Due");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_Status_LeaseExpiresAt");
                e.HasIndex(x => x.ObjectKey)
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Pending', 'Processing')")
                    .HasDatabaseName("UX_AttachmentBlobDeleteJob_ActiveObjectKey");
                e.HasIndex(x => x.UserId)
                    .HasDatabaseName("IX_AttachmentBlobDeleteJob_UserId");
            });

            builder.Entity<AttachmentScanJob>(e =>
            {
                e.ToTable("T_AttachmentScanJob");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.AttachmentId).HasMaxLength(64).IsRequired();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.ContentType).HasMaxLength(128);
                e.Property(x => x.OriginalName).HasMaxLength(256);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AttachmentScanJob_Due");
                e.HasIndex(x => x.AttachmentId)
                    .HasDatabaseName("IX_AttachmentScanJob_AttachmentId");
                e.HasIndex(x => x.UserId)
                    .HasDatabaseName("IX_AttachmentScanJob_UserId");
                e.HasIndex(x => x.AttachmentId)
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Pending', 'Processing', 'Finalizing')")
                    .HasDatabaseName("IX_AttachmentScanJob_ActiveAttachment");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_AttachmentScanJob_LeaseDue");
            });

            builder.Entity<AttachmentScanAudit>(e =>
            {
                e.ToTable("T_AttachmentScanAudit");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.AttachmentId).HasMaxLength(64).IsRequired();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.ContentType).HasMaxLength(128);
                e.Property(x => x.EngineName).HasMaxLength(128).IsRequired();
                e.Property(x => x.EngineVersion).HasMaxLength(128).IsRequired();
                e.Property(x => x.Verdict).HasMaxLength(32).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(500);
                e.HasIndex(x => new { x.AttachmentId, x.CreatedAt })
                    .HasDatabaseName("IX_AttachmentScanAudit_Attachment_Created");
                e.HasIndex(x => new { x.ScanJobId, x.CreatedAt })
                    .HasDatabaseName("IX_AttachmentScanAudit_Job_Created");
            });

            builder.Entity<AttachmentScanProjection>(e =>
            {
                e.ToTable("T_AttachmentScanProjection");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.AttachmentId).HasMaxLength(64).IsRequired();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.ContentType).HasMaxLength(128);
                e.Property(x => x.OriginalName).HasMaxLength(256);
                e.Property(x => x.ContentHash).HasMaxLength(64);
                e.Property(x => x.SourceEntityTag).HasMaxLength(256);
                e.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
                e.Property(x => x.RejectionReason).HasMaxLength(500);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AttachmentScanProjection_Due");
                e.HasIndex(x => x.ScanJobId)
                    .HasDatabaseName("IX_AttachmentScanProjection_ScanJob");
                e.HasIndex(x => x.ScanJobId)
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Pending', 'Processing')")
                    .HasDatabaseName("UX_AttachmentScanProjection_ActiveScanJob");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_AttachmentScanProjection_LeaseDue");
            });

            builder.Entity<AttachmentConfirmSaga>(e =>
            {
                e.ToTable("T_AttachmentConfirmSaga");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.AttachmentId).HasMaxLength(64).IsRequired();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.ProtectedTicket).HasMaxLength(2048);
                e.Property(x => x.ConfirmedObjectKey).HasMaxLength(512);
                e.Property(x => x.ContentType).HasMaxLength(128);
                e.Property(x => x.OriginalName).HasMaxLength(256);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => x.AttachmentId)
                    .IsUnique()
                    .HasDatabaseName("UX_AttachmentConfirmSaga_AttachmentId");
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AttachmentConfirmSaga_Due");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_AttachmentConfirmSaga_LeaseDue");
                e.HasIndex(x => x.UserId)
                    .HasDatabaseName("IX_AttachmentConfirmSaga_UserId");
            });

            builder.Entity<AvatarFinalizationSaga>(e =>
            {
                e.ToTable("T_AvatarFinalizationSaga");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).UseIdentityByDefaultColumn();
                e.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
                e.Property(x => x.ProtectedTicket).HasMaxLength(2048);
                e.Property(x => x.OldAvatarUrl).HasMaxLength(1024);
                e.Property(x => x.FinalObjectKey).HasMaxLength(512);
                e.Property(x => x.PublicUrl).HasMaxLength(1024);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.Property(x => x.LeaseOwner).HasMaxLength(128);
                e.Property(x => x.LeaseToken).HasMaxLength(64);
                e.HasIndex(x => new { x.UserId, x.ObjectKey })
                    .IsUnique()
                    .HasDatabaseName("UX_AvatarFinalizationSaga_User_Object");
                e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                    .HasDatabaseName("IX_AvatarFinalizationSaga_Due");
                e.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                    .HasDatabaseName("IX_AvatarFinalizationSaga_LeaseDue");
                e.HasIndex(x => x.UserId)
                    .HasDatabaseName("IX_AvatarFinalizationSaga_UserId");
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
            builder.ApplyConfiguration(new EmailOutboxItemConfig());
            builder.ApplyConfiguration(new NotificationOutboxItemConfig());
            builder.ApplyConfiguration(new ModerationSessionRevocationOutboxItemConfig());
            builder.ApplyConfiguration(new FriendshipConfig());
            builder.ApplyConfiguration(new FriendRequestConfig());
            builder.ApplyConfiguration(new FriendGroupConfig());
            builder.ApplyConfiguration(new BlockRecordConfig());
            builder.AddChatAppRealtimeOutbox();
        }
    }
}
