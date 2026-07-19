using Core.Interfaces;
using Core.Interfaces.Cache;
using Infrastructure.Caching;
using Infrastructure.Data;
using Infrastructure.Serialization;
using Infrastructure.Messaging;
using Infrastructure.Services.Utilities;
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
            services.AddSingleton<RealtimeDomainOutboxInterceptor>();
            // 连接池 + 命令超时；DbContextPool 降低上下文分配开销
            services.AddDbContextPool<UserDbContext>((serviceProvider, options) => options
                .UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.CommandTimeout(15);
                    npgsql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
                })
                .AddInterceptors(serviceProvider.GetRequiredService<RealtimeDomainOutboxInterceptor>()));

            return services;
        }

        public async Task<IServiceCollection> AddRedisCacheServices(IConfiguration configuration)
        {

            await services.AddGarnet(configuration);
            services.AddSerializer();
            // 从配置文件 GarnetCache 节绑定选项，而不是使用默认实例
            services.Configure<RedisCacheOptions>(configuration.GetSection(RedisCacheOptions.SectionName));
            services.AddSingleton<ICacheProvider, RedisCaching>();
            return services;
        }

        private void AddSerializer()
        {
            services.AddSingleton<ISerializer, TextJsonSerializer>();
        }

        /// <summary>
        /// 添加 Garnet（Redis 兼容）缓存服务到服务集合中
        /// </summary>
        private async Task AddGarnet(IConfiguration configuration)
        {
            var connStr = configuration.GetConnectionString("Garnet");
            var configurationOptions = ConfigurationOptions.Parse(connStr ?? throw new InvalidOperationException("未找到 Garnet 连接字符串"), true);
            configurationOptions.ResolveDns = false;
            // 总重试预算控制在约 1s 内，缓存故障时快速失败返回 503，避免打满线程池。
            configurationOptions.ConnectTimeout = 1000;
            configurationOptions.SyncTimeout = 1000;
            configurationOptions.AbortOnConnectFail = false;
            configurationOptions.ConnectRetry = 1;
            configurationOptions.KeepAlive = 180;

            var multiplexer = await ConnectionMultiplexer.ConnectAsync(configurationOptions).ConfigureAwait(false);
            services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        }
    }
}
