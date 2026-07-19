using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data
{
    internal class UserDbContextFactory : IDesignTimeDbContextFactory<UserDbContext>
    {
        /// <summary>
        /// 设计时 DbContext 工厂，用于在设计时创建 UserDbContext 实例，主要用于迁移和其他设计时操作
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public UserDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<UserDbContext>();

            optionsBuilder.UseNpgsql("Host=127.0.0.1;Port=5432;Database=ChatAppDatabase;Username=postgres;Password=520666");

            return new UserDbContext(optionsBuilder.Options);
        }
    }
}
