using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data;

internal sealed class UserDbContextFactory : IDesignTimeDbContextFactory<UserDbContext>
{
    public UserDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("CHATAPP_TEST_POSTGRES")
            ?? throw new InvalidOperationException(
                "设计时迁移需要连接串：请设置环境变量 ConnectionStrings__DefaultConnection，或先加载项目根目录 .env。");

        var optionsBuilder = new DbContextOptionsBuilder<UserDbContext>();
        var timeoutText = Environment.GetEnvironmentVariable(
            "DatabasePool__MigrationCommandTimeoutSeconds");
        var timeoutSeconds = int.TryParse(timeoutText, out var configured)
            ? Math.Clamp(configured, 30, 600)
            : 120;
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
            npgsql.CommandTimeout(timeoutSeconds));
        return new UserDbContext(optionsBuilder.Options);
    }
}
