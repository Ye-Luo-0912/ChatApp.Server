using System.Security.Claims;
using System.Text.Encodings.Web;
using Core.Exceptions;
using Core.Interfaces.Auth;
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
    IAccessTokenStore tokenStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 提取 Authorization 头
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        var header = headerValues.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        try
        {
            // 认证热路径：每次请求仅一次缓存读取。
            var data = await tokenStore.GetAccessTokenAsync(token);
            if (data is null)
                return AuthenticateResult.Fail("令牌无效或不存在");

            if (data.IsExpired)
            {
                await tokenStore.RevokeAccessTokenAsync(token);
                return AuthenticateResult.Fail("令牌已过期");
            }

            var roleCount = data.Roles?.Length ?? 0;
            var claims = new List<Claim>(roleCount + 2)
            {
                new(ClaimTypes.NameIdentifier, data.UserId.ToString()),
                new(ClaimTypes.Name, data.UserName),
            };

            if (data.Roles is not null)
                claims.AddRange(data.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

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
    }
}
