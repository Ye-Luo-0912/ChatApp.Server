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
        /// 添加用户数据库上下文和身份验证服务。
        /// 连接串在解析 DbContext 时从 <see cref="IConfiguration"/> 读取，
        /// 以便 WebApplicationFactory 的 ConfigureAppConfiguration 覆盖能生效。
        /// </summary>
        public IServiceCollection AddUserDbContext(IConfiguration configuration)
        {
            services.AddSingleton<ITsidGenerator, TsidGeneratorService>();
            services.AddSingleton<RealtimeDomainOutboxInterceptor>();
            services.AddDbContextPool<UserDbContext>((serviceProvider, options) =>
            {
                var connectionString = serviceProvider
                    .GetRequiredService<IConfiguration>()
                    .GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException("缺少 ConnectionStrings:DefaultConnection");

                options.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.CommandTimeout(15);
                }).AddInterceptors(serviceProvider.GetRequiredService<RealtimeDomainOutboxInterceptor>());
            });

            return services;
        }

        /// <summary>
        /// 注册 Redis/Garnet 缓存。连接在首次解析 <see cref="IConnectionMultiplexer"/> 时建立，
        /// 避免在测试配置注入前连接错误的地址。
        /// </summary>
        public IServiceCollection AddRedisCacheServices(IConfiguration configuration)
        {
            services.AddGarnet();
            services.AddSerializer();
            services.AddOptions<RedisCacheOptions>()
                .Configure<IConfiguration>((opts, configuration) =>
                    configuration.GetSection(RedisCacheOptions.SectionName).Bind(opts));
            services.AddSingleton<RedisCacheStore>();
            services.AddSingleton<ICacheValueStore>(sp => sp.GetRequiredService<RedisCacheStore>());
            services.AddSingleton<IAtomicCacheStore>(sp => sp.GetRequiredService<RedisCacheStore>());
            services.AddSingleton<ICacheSetStore>(sp => sp.GetRequiredService<RedisCacheStore>());
            return services;
        }

        private void AddSerializer()
        {
            services.AddSingleton<ISerializer, TextJsonSerializer>();
        }

        private void AddGarnet()
        {
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var connStr = configuration.GetConnectionString("Garnet");
                if (string.IsNullOrWhiteSpace(connStr))
                    throw new InvalidOperationException("未找到 Garnet 连接字符串");

                var configurationOptions = ConfigurationOptions.Parse(connStr, true);
                configurationOptions.ResolveDns = false;
                configurationOptions.ConnectTimeout = 1000;
                configurationOptions.SyncTimeout = 1000;
                configurationOptions.AsyncTimeout = 1000;
                configurationOptions.AbortOnConnectFail = false;
                configurationOptions.ConnectRetry = 1;
                configurationOptions.KeepAlive = 180;

                return ConnectionMultiplexer.Connect(configurationOptions);
            });
        }
    }
}
