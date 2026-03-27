using System.Text.RegularExpressions;
using Core.Models.Friend;
using Core.Models.Identity;
using Infrastructure.Models.Config;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Models.DbContext
{
    public class UserDbContext(DbContextOptions<UserDbContext> options) : IdentityDbContext<ApplicationUser, ApplicationRoles, long>(options)
    {
        public DbSet<UserFriendEntry> Friendships { get; set; }
        public DbSet<FriendRequest> FriendRequests { get; set; }
        public DbSet<BlockRecord> BlockRecords { get; set; }
        public DbSet<FriendGroup> FriendGroups { get; set; }

    
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<ApplicationUser>()
            .Property(u => u.Id)
            .ValueGeneratedNever();// 取消自动生成，改为手动设置


            builder.Entity<ApplicationRoles>()
               .Property(r => r.Id)
               .ValueGeneratedNever();

            builder.Ignore<Capture>();
            builder.ApplyConfiguration(new FriendshipConfig());
            builder.ApplyConfiguration(new FriendRequestConfig());
            builder.ApplyConfiguration(new BlockRecordConfig());
            builder.ApplyConfiguration(new FriendGroupConfig());
        }

    /*    // 1. 定义生成器
        public class GuidV7Generator : ValueGenerator<Guid>
        {
            public override bool GeneratesTemporaryValues => false;
            public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
        }*/


    }
}