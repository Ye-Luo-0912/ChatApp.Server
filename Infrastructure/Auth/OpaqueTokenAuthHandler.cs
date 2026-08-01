using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Core.Exceptions;
using Core.Interfaces.Auth;
using Infrastructure.Data;
using Core.Models.Token;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Auth;

/// <summary>
/// 自定义不透明令牌（Opaque Token）认证处理器，注册为 "Bearer" 方案。
/// <para>
/// 工作流程：
/// <list type="number">
///   <item>从请求头 <c>Authorization: Bearer &lt;token&gt;</c> 提取令牌字符串。</item>
///   <item>通过 <see cref="IAccessTokenStore"/> 在 Redis 中查询令牌元数据（<c>AccessTokenData</c>）。</item>
///   <item>将元数据中的 UserId、UserName、Roles 等写入 <see cref="ClaimsPrincipal"/>，行为与原 JWT 中间件一致。</item>
/// </list>
/// </para>
/// <para>
/// 注册方案名为 <c>"Bearer"</c>，与 <c>JwtBearerDefaults.AuthenticationScheme</c> 值相同，
/// 因此无需修改任何控制器上的 <c>[Authorize]</c> 特性。
/// </para>
/// </summary>
public sealed class OpaqueTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccessTokenStore tokenStore,
    UserDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 提取 Authorization 头
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        if (headerValues.Count == 0)
            return AuthenticateResult.NoResult();

        // 只保留一次 token 字符串分配：避免 StringValues.ToString() 后再切片复制。
        var header = headerValues[0];
        var headerSpan = header.AsSpan();
        const string bearerPrefix = "Bearer ";
        if (headerSpan.Length <= bearerPrefix.Length
            || !headerSpan.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var tokenSpan = headerSpan[bearerPrefix.Length..].Trim();
        if (tokenSpan.IsEmpty)
            return AuthenticateResult.NoResult();

        // access token 始终由 Generate(16) 产生。先校验长度和 Base64url 字符集，
        // 避免任意长 Authorization header 进入 Redis key 哈希路径。
        if (!OpaqueTokenFormat.IsAccessToken(tokenSpan))
            return AuthenticateResult.Fail("令牌格式无效");

        var token = tokenSpan.ToString();

        try
        {
            // 认证热路径：每次请求仅一次缓存读取。
            var data = await tokenStore.GetAccessTokenAsync(token, Context.RequestAborted);
            if (data is null)
                return AuthenticateResult.Fail("令牌无效或不存在");

            if (data.IsExpired)
            {
                await tokenStore.RevokeAccessTokenAsync(token, Context.RequestAborted);
                return AuthenticateResult.Fail("令牌已过期");
            }


            // Redis token deletion and the PostgreSQL security mutation cannot be
            // committed atomically. The durable SecurityVersion is therefore the
            // authorization fence: a stale AT must fail even if session revocation
            // previously suffered an ambiguous/failed Redis write.
            var now = DateTimeOffset.UtcNow;
            var state = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == data.UserId)
                .Select(u => new
                {
                    u.SecurityVersion,
                    u.LockoutEnabled,
                    u.LockoutEnd,
                    u.BanUntil,
                    u.DeletionScheduledAt,
                })
                .SingleOrDefaultAsync(Context.RequestAborted);
            if (state is null
                || data.SecurityVersion <= 0
                || state.SecurityVersion != data.SecurityVersion
                || (state.LockoutEnabled && state.LockoutEnd != null && state.LockoutEnd > now)
                || state.DeletionScheduledAt != null
                || state.BanUntil > now)
            {
                try
                {
                    await tokenStore.RevokeAccessTokenAsync(token, Context.RequestAborted);
                }
                catch (CacheUnavailableException ex)
                {
                    Logger.LogWarning(ex, "清理失效访问令牌失败 UserId={UserId}", data.UserId);
                }

                return AuthenticateResult.Fail("令牌安全版本已失效");
            }
            var roleCount = data.Roles?.Length ?? 0;
            var claims = new List<Claim>(roleCount + 4)
            {
                new(ClaimTypes.NameIdentifier, data.UserId.ToString()),
                new(ClaimTypes.Name, data.UserName),
            };

            if (!string.IsNullOrWhiteSpace(data.SessionId))
                claims.Add(new Claim(Core.Models.Auth.AuthClaimTypes.SessionId, data.SessionId));
            if (data.DeviceIdHash is { } didh)
                claims.Add(new Claim(Core.Models.Auth.AuthClaimTypes.DeviceIdHash, didh.ToString("x16")));

            if (data.Roles is { Length: > 0 } roles)
            {
                foreach (var role in roles)
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (CacheUnavailableException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return AuthenticateResult.Fail("缓存服务不可用");
        }
        catch (DbException ex)
        {
            Logger.LogError(ex, "认证安全版本查询失败");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return AuthenticateResult.Fail("认证数据库不可用");
        }
    }
}
