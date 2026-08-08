using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Core.Exceptions;
using Core.Interfaces.Auth;
using Core.Models.Token;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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
///   <item>将令牌绑定字段与用户级授权快照中的 UserName、Roles 写入 <see cref="ClaimsPrincipal"/>。</item>
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
    IUserAuthorizationFence authSnapshots)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string FailureKindItem = "ChatApp.AuthenticationFailureKind";

    private enum AuthenticationFailureKind : byte
    {
        DependencyUnavailable = 1,
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 提取 Authorization 头
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        if (headerValues.Count == 0)
            return AuthenticateResult.NoResult();

        // 只引用原始 Authorization 字符串；后续用 ReadOnlyMemory<char>
        // 传给令牌存储，避免为 token substring 创建新的 string。
        var header = headerValues[0];
        var headerSpan = header.AsSpan();
        const string bearerPrefix = "Bearer ";
        if (headerSpan.Length <= bearerPrefix.Length
            || !headerSpan.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var tokenStart = bearerPrefix.Length;
        while (tokenStart < headerSpan.Length
               && char.IsWhiteSpace(headerSpan[tokenStart]))
            tokenStart++;

        var tokenEnd = headerSpan.Length;
        while (tokenEnd > tokenStart
               && char.IsWhiteSpace(headerSpan[tokenEnd - 1]))
            tokenEnd--;

        var tokenSpan = headerSpan[tokenStart..tokenEnd];
        if (tokenSpan.IsEmpty)
            return AuthenticateResult.NoResult();

        // access token 始终由 Generate(16) 产生。先校验长度和 Base64url 字符集，
        // 避免任意长 Authorization header 进入 Redis key 哈希路径。
        if (!OpaqueTokenFormat.IsAccessToken(tokenSpan))
            return AuthenticateResult.Fail("令牌格式无效");

        var tokenMemory = header.AsMemory(tokenStart, tokenEnd - tokenStart);

        try
        {
            // 认证热路径：每次请求仅一次缓存读取。
            var data = await tokenStore.GetAccessTokenAsync(tokenMemory, Context.RequestAborted);
            if (data is null)
                return AuthenticateResult.Fail("令牌无效或不存在");

            if (data.IsExpired)
            {
                await tokenStore.RevokeAccessTokenAsync(tokenMemory, Context.RequestAborted);
                return AuthenticateResult.Fail("令牌已过期");
            }


            // The token L1 and the short-lived auth-fence L1 are both process-local.
            // A warm ordinary request therefore performs no PostgreSQL round trip;
            // the fence store only reaches Garnet/DB when its own L1 entry misses.
            var now = DateTimeOffset.UtcNow;
            var snapshot = await authSnapshots.GetFenceAsync(data.UserId, Context.RequestAborted)
                .ConfigureAwait(false);
            if (snapshot is null
                || data.SecurityVersion <= 0
                || snapshot.SecurityVersion != data.SecurityVersion
                || !snapshot.IsAllowedAt(now))
            {
                try
                {
                    await tokenStore.RevokeAccessTokenAsync(tokenMemory, Context.RequestAborted);
                }
                catch (CacheUnavailableException ex)
                {
                    Logger.LogWarning(ex, "清理失效访问令牌失败 UserId={UserId}", data.UserId);
                }

                return AuthenticateResult.Fail("令牌安全版本已失效");
            }
            // New ATs intentionally do not carry repeated username/role data.
            // The fence store guarantees a complete snapshot on its first
            // miss; the legacy fallback keeps already-issued ATs readable.
            var userName = snapshot.UserName ?? data.UserName;
            if (userName is null || !snapshot.ClaimsLoaded)
                return AuthenticateResult.Fail("认证快照不完整");

            var roles = snapshot.Roles;
            var roleCount = roles.Length;
            var claims = new List<Claim>(roleCount + 4)
            {
                new(ClaimTypes.NameIdentifier, data.UserIdText),
                new(ClaimTypes.Name, userName),
            };

            if (!string.IsNullOrWhiteSpace(data.SessionId))
                claims.Add(new Claim(Core.Models.Auth.AuthClaimTypes.SessionId, data.SessionId));
            if (data.DeviceIdHashText is { } deviceIdHashText)
                claims.Add(new Claim(Core.Models.Auth.AuthClaimTypes.DeviceIdHash, deviceIdHashText));

            if (roles is { Length: > 0 })
            {
                foreach (var role in roles)
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var accountState = snapshot.EffectiveAccountState(now);
            claims.Add(new Claim(
                Core.Models.Auth.AuthClaimTypes.AccountState,
                accountState.ToString()));
            if (accountState == Core.Models.Identity.AccountState.DeletionPending
                && snapshot.DeletionScheduledAt is { } scheduledAt)
            {
                claims.Add(new Claim(
                    Core.Models.Auth.AuthClaimTypes.DeletionScheduledAt,
                    scheduledAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (CacheUnavailableException)
        {
            MarkDependencyUnavailable();
            return AuthenticateResult.Fail("缓存服务不可用");
        }
        catch (CacheCorruptedException)
        {
            MarkDependencyUnavailable();
            return AuthenticateResult.Fail("缓存服务不可用");
        }
        catch (CacheSerializationException)
        {
            MarkDependencyUnavailable();
            return AuthenticateResult.Fail("缓存服务不可用");
        }
        catch (DbException ex)
        {
            Logger.LogError(ex, "认证安全版本查询失败");
            MarkDependencyUnavailable();
            return AuthenticateResult.Fail("认证数据库不可用");
        }
    }

    /// <summary>
    /// Authentication failure and authorization challenge are separate ASP.NET
    /// Core phases. Preserve a dependency outage through the request so the
    /// challenge cannot turn it into a misleading 401.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(FailureKindItem, out var value)
            && value is AuthenticationFailureKind.DependencyUnavailable)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            Response.Headers.RetryAfter = "1";
            return Task.CompletedTask;
        }

        // Missing/invalid credentials retain the normal 401 challenge. An
        // authorization failure after successful authentication remains 403.
        return base.HandleChallengeAsync(properties);
    }

    private void MarkDependencyUnavailable()
        => Context.Items[FailureKindItem] = AuthenticationFailureKind.DependencyUnavailable;
}
