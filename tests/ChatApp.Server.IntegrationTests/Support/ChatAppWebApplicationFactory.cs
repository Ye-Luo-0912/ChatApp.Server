using Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 基于真实 Postgres + Garnet 的 HTTP 管道工厂；共享连接串可模拟多实例。
/// </summary>
public sealed class ChatAppWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _postgres;
    private readonly string _redis;
    private readonly string _keyPrefix;
    private readonly string _avatarRoot;
    private readonly Dictionary<string, string?> _extra;

    public ChatAppWebApplicationFactory(
        string postgresConnection,
        string redisConnection,
        string? cacheKeyPrefix = null,
        string? avatarRoot = null,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        _postgres = postgresConnection;
        _redis = redisConnection;
        _keyPrefix = cacheKeyPrefix ?? $"waf:{Guid.NewGuid():N}:";
        _avatarRoot = avatarRoot ?? Path.Combine(Path.GetTempPath(), "chatapp-waf-avatars", Guid.NewGuid().ToString("N"));
        _extra = extraConfig is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(extraConfig);
        Directory.CreateDirectory(_avatarRoot);
    }

    public string CacheKeyPrefix => _keyPrefix;
    public string AvatarRoot => _avatarRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres,
                ["ConnectionStrings:Garnet"] = _redis,
                ["GarnetCache:KeyPrefix"] = _keyPrefix,
                ["GarnetCache:DefaultSlidingExpiration"] = "00:30:00",
                ["GarnetCache:ExpirationJitterPercent"] = "0",
                ["GarnetCache:LockTimeout"] = "00:00:05",
                ["GarnetCache:DefaultLockExpiry"] = "00:00:03",
                ["JwtSettings:Issuer"] = "ChatApp",
                ["JwtSettings:Audience"] = "ChatApp",
                ["JwtSettings:Secret"] = "waf-test-secret-please-change-32chars",
                ["JwtSettings:AccessTokenExpirationMinutes"] = "30",
                ["JwtSettings:RefreshTokenLength"] = "32",
                ["JwtSettings:RefreshTokenExpirationDays"] = "3",
                ["Security:SecretEncryptionKey"] = "waf-test-mfa-encryption-key-32c",
                ["Security:KeyVersion"] = "1",
                ["RealtimeGateway:Host"] = "127.0.0.1",
                ["RealtimeGateway:Port"] = "8888",
                ["RealtimeGateway:Name"] = "waf-test",
                ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
                ["ForwardedHeaders:KnownNetworks:0"] = "172.28.0.0/16",
                ["EmailSettings:Host"] = "",
                ["EmailSettings:Port"] = "465",
                ["EmailSettings:SenderEmail"] = "",
                ["EmailSettings:SenderName"] = "ChatApp",
                ["EmailSettings:Password"] = "",
                ["AvatarStorage:Provider"] = "Local",
                ["AvatarStorage:MaxBytes"] = "2097152",
                ["AvatarStorage:LocalRootPath"] = _avatarRoot,
                ["AvatarStorage:PublicBaseUrl"] = "/static/avatars",
                ["AvatarStorage:TicketMinutes"] = "15",
                ["AvatarStorage:AllowedContentTypes:0"] = "image/jpeg",
                ["AvatarStorage:AllowedContentTypes:1"] = "image/png",
                ["AvatarStorage:AllowedContentTypes:2"] = "image/webp",
                ["EnableHttpsRedirection"] = "false",
                ["Cors:AllowedOrigins:0"] = "http://localhost",
                ["RateLimiting:AuthLoginPermitLimit"] = "1000",
                ["RateLimiting:AuthLoginWindowSeconds"] = "60",
                ["RateLimiting:AuthRefreshPermitLimit"] = "1000",
                ["RateLimiting:AuthRefreshWindowSeconds"] = "60",
                ["RateLimiting:AuthEmailPermitLimit"] = "1000",
                ["RateLimiting:AuthEmailWindowSeconds"] = "60",
                ["RateLimiting:UserEmailChangePermitLimit"] = "1000",
                ["RateLimiting:UserEmailChangeWindowSeconds"] = "60",
                ["RateLimiting:UserSensitivePermitLimit"] = "1000",
                ["RateLimiting:UserSensitiveWindowSeconds"] = "60",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            };

            foreach (var (k, v) in _extra)
                values[k] = v;

            config.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            // 测试中不跑后台 Worker，避免抢占 Outbox / 安全事件归档。
            services.RemoveAll<IHostedService>();
            // 避免登录安全通知写入 EmailOutbox，污染 Outbox 集成测试。
            services.RemoveAll<ISecurityNotificationService>();
            services.AddSingleton<ISecurityNotificationService, NoopSecurityNotificationService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (Directory.Exists(_avatarRoot))
                Directory.Delete(_avatarRoot, recursive: true);
        }
        catch
        {
            // ignore cleanup
        }
    }
}

internal sealed class NoopSecurityNotificationService : ISecurityNotificationService
{
    public void StageNotify(long userId, string type, string title, string body, bool preferEmail) { }

    public Task NotifyAsync(
        long userId, string type, string title, string body, bool preferEmail,
        CancellationToken cancellationToken = default, string? idempotencyKey = null)
        => Task.CompletedTask;
}
