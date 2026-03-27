using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatApp.Server.ConfigServiceLibrary.Data
{
    /// <summary>
    /// 设计时创建ConfigDbContext的工厂类
    /// </summary>
    public class ConfigDbContextFactory : IDesignTimeDbContextFactory<ConfigDbContext>
    {
        /// <summary>
        /// 创建配置数据库上下文实例
        /// </summary>
        public ConfigDbContext CreateDbContext(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<ConfigDbContext>();
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            optionsBuilder.UseSqlite(connectionString);

            return new ConfigDbContext(optionsBuilder.Options);
        }
    }
}