using System.Text.RegularExpressions;
using Core.Models.Friend;
using Core.Models.Identity;
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

            builder.Ignore<Capture>();
            builder.ApplyConfiguration(new FriendshipConfig());
            builder.ApplyConfiguration(new FriendRequestConfig());
            builder.ApplyConfiguration(new BlockRecordConfig());
            builder.ApplyConfiguration(new FriendGroupConfig());
            builder.AddChatAppRealtimeOutbox();
        }
    }
}
