using ChatApp.Server.ConfigServiceLibrary.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Server.ConfigServiceLibrary.Data;

/// <summary>
/// 配置数据库上下文，负责数据访问功能和实体配置
/// </summary>
public class ConfigDbContext(DbContextOptions<ConfigDbContext> options) : DbContext(options)
{
    /// <summary>
/// 配置项集合
/// </summary>
public DbSet<ConfigItem> ConfigItem { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigItem>()
            .HasKey(c => c.Id);
    }
}