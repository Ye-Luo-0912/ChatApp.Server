using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Identity;
using Infrastructure.Caching;
using Infrastructure.Models.DbContext;
using Infrastructure.Serializer;
using Infrastructure.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Infrastructure.Extensions;

public static class InfrastructureExtensions
{
    /// <summary>
    /// 提供了扩展方法，用于在应用程序中注册用户数据库上下文、身份验证服务以及Redis缓存服务。
    /// </summary>
    extension(IServiceCollection services)
    {
        /// <summary>
        /// 添加用户数据库上下文和身份验证服务
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public IServiceCollection AddUserDbContext(IConfiguration configuration)
        {
            // 从配置中获取数据库连接字符串
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            // 注册 TSID 生成器为单例服务
            services.AddSingleton<ITsidGenerator, TsidGeneratorService>();
            // 注册 UserDbContext，使用 PostgreSQL 数据库
            services.AddDbContext<UserDbContext>(op=>op.UseNpgsql(connectionString));

            // 注册 ASP.NET Core Identity 服务，使用 ApplicationUser 和 ApplicationRoles，并指定 UserDbContext 作为存储
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<ApplicationRoles>()
                .AddEntityFrameworkStores<UserDbContext>();

            return services;
        }

        public async Task<IServiceCollection> AddRedisCacheServices(IConfiguration configuration)
        {

            await services.AddRedis(configuration);
            services.AddSerializer();
            services.AddSingleton<RedisCacheOptions>();
            services.AddSingleton<ICacheProvider, RedisCaching>();
            return services;
        }

        private void AddSerializer()
        {
            services.AddSingleton<ISerializer, TextJsonSerializer>();
        }

        /// <summary>
        /// 添加Redis缓存服务到服务集合中
        /// </summary>
        /// <param name="configuration">应用程序配置，用于获取Redis连接字符串和其他相关设置</param>
        /// <returns>包含已注册Redis缓存服务的服务集合</returns>
        private async Task AddRedis(IConfiguration configuration)
        {
            var redisConnStr = configuration.GetConnectionString("Redis");
            var configurationOptions = ConfigurationOptions.Parse(redisConnStr ?? throw new InvalidOperationException(), true);
            //configurationOptions.ResolveDns = true;
            configurationOptions.ResolveDns = false;      // 本地开发必关！直接用 IP 连，不走 DNS
            configurationOptions.ConnectTimeout = 3000;   // 3 秒没连上就立刻报错
            configurationOptions.SyncTimeout = 3000;      // 同步操作超时
            configurationOptions.AbortOnConnectFail = false;
            configurationOptions.ConnectRetry = 3;
            configurationOptions.KeepAlive = 180;

            var multiplexer = await ConnectionMultiplexer.ConnectAsync(configurationOptions).ConfigureAwait(false);
            services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        }
    }
}