using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.DTOs.Auth;
using Core.Models.DTOs.Login;
using Core.Models.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Services;

public class JwtTokenServices : IJwtTokenService
{
    private const string RefreshTokenPrefix = "RT:";
    private readonly ICacheProvider _cacheProvider;
    private readonly IDeviceInfo _deviceInfo;
    private readonly ILogger<JwtTokenServices> _logger;
    private readonly JwtSettings _settings;


    private readonly SigningCredentials _signingCredentials;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly TokenValidationParameters _validationParameters;

    public JwtTokenServices(IOptions<JwtSettings> options, ICacheProvider cacheProvider, IDeviceInfo deviceInfo, ILogger<JwtTokenServices> logger)
    {
        _cacheProvider = cacheProvider;
        _settings = options.Value;
        _deviceInfo = deviceInfo;
        _logger = logger;

        if (Encoding.UTF8.GetBytes(_settings.Secret).Length < 32)
            throw new ArgumentException("密钥长度至少需要256位 (32字节)");

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Secret));
        _signingCredentials = new SigningCredentials(
            signingKey,
            SecurityAlgorithms.HmacSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _settings.Issuer,
            ValidateAudience = true,
            ValidAudience = _settings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };

    }


    /// <summary>
    ///     创建登录Token
    /// </summary>
    /// <param name="user">用户</param>
    /// <param name="roles">角色</param>
    /// <returns></returns>
    public string GenerateAccessToken(ApplicationUser user, IList<string>? roles = null)
    {
        // 访问令牌只放认证和授权需要的最小声明，避免载荷膨胀。
        var claims = new List<Claim>((roles?.Count ?? 0) + 4)
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(JwtRegisteredClaimNames.Name, user.UserName ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64) // 签发时间
        };

        // 添加角色声明
        if (roles?.Any() is true) claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpirationMinutes),
            SigningCredentials = _signingCredentials,
            Issuer = _settings.Issuer,
            Audience = _settings.Audience
        };

        var securityToken = _tokenHandler.CreateToken(tokenDescriptor);
        return _tokenHandler.WriteToken(securityToken);
    }

    /// <summary>
    ///     创建刷新Token
    /// </summary>
    /// <returns></returns>
    public string GenerateRefreshToken()
    {
        var randomNumber = new byte[_settings.RefreshTokenLength];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    ///     Token验证
    /// </summary>
    /// <param name="token"></param>
    /// <param name="principal"></param>
    /// <returns></returns>
    public bool ValidateToken(string token, out ClaimsPrincipal? principal)
    {
        principal = null;

        if (string.IsNullOrWhiteSpace(token) || !_tokenHandler.CanReadToken(token))
            return false;

        try
        {
            principal = _tokenHandler.ValidateToken(token, _validationParameters, out _);
            return true;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogInformation(ex, "JWT 校验失败");
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation(ex, "JWT 格式非法");
            return false;
        }
    }

    public async Task StoreRefreshTokenAsync(string userId, string refreshToken)
    {
        // 刷新令牌会绑定当前设备信息，后续刷新时需要同一设备才能通过。
        var devices = _deviceInfo.GenerateDeviceInfo();
        var expiryTime = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);

        // Redis 键只使用哈希后的 token，避免把原始 refresh token 暴露在键名里。
        var token = TokenKey(userId, refreshToken);

        _logger.LogWarning("准备存入 Redis！过期时间(天): {Days}", expiryTime.TotalDays);

        var newTokenData = new RefreshToken
        {
            UserId = userId,
            DeviceId = devices.DeviceId,
            DeviceName = devices.DeviceName,
            Token = token,
            ExpiresAt = DateTime.UtcNow.Add(expiryTime),
            ClientIp = devices.IpAddress
        };

        // 服务端保留 refresh token 元数据，后续校验、撤销和轮换都依赖这里的数据。
        await _cacheProvider.SetAsync(token, newTokenData, absoluteExpiration: expiryTime);
    }

    /// <summary>
    /// 验证刷新令牌是否合法（存在且未过期），但不涉及设备绑定和销毁逻辑
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="refreshToken"></param>
    /// <returns></returns>
    public async Task<bool> ValidateRefreshTokenAsync(string userId, string refreshToken)
    {
        // 先确认服务端还持有这张刷新令牌，并且它还没过期。
        var refreshTokenData = await GetRefreshToken(userId, refreshToken);
        if (refreshTokenData is null || refreshTokenData.ExpiresAt < DateTime.UtcNow) return false;

        // 再校验是否由同一设备发起，避免 refresh token 被跨设备滥用。
        var deviceId = _deviceInfo.GetDeviceId();
        if (deviceId is null || refreshTokenData.DeviceId != deviceId) return false;

        return true; // 确认旧票有效
    }

    /// <summary>
    /// 撤销刷新令牌：从存储中删除指定用户和令牌的记录，使其失效
    /// </summary>
    /// <param name="userId">The unique identifier of the user whose refresh token is to be revoked. Cannot be null or empty.</param>
    /// <param name="refreshToken">The refresh token to revoke. Cannot be null or empty.</param>
    /// <returns>A task that represents the asynchronous revoke operation.</returns>
    public async Task RevokeRefreshTokenAsync(string userId, string refreshToken)
    {
        await _cacheProvider.RemoveAsync(TokenKey(userId, refreshToken));
    }

    /// <summary>
    /// 获取关联用户的刷新令牌信息
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="refreshToken"></param>
    /// <returns></returns>
    public async Task<RefreshToken?> GetRefreshTokenAsync(string userId, string refreshToken)
    {
        return await GetRefreshToken(userId, refreshToken);
    }

    /*    public async Task<TokenPairResult> RefreshTokensAsync(string userId, string refreshToken)
        {
            var refreshTokenData = await GetRefreshToken(userId, refreshToken);
            if (refreshTokenData is null || refreshTokenData.ExpiresAt < DateTime.UtcNow) 
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            // 再校验是否由同一设备发起，避免 refresh token 被跨设备滥用。
        var deviceId = _deviceInfo.GetDeviceId();
            if (deviceId is null || refreshTokenData.DeviceId != deviceId)
                return TokenPairResult.Fail(AuthErrorType.DeviceMismatch);

            try
            {
                var newRefreshToken = GenerateRefreshToken();
                var newAccessToken = GenerateAccessToken(new ApplicationUser { Id = Guid.Parse(userId)});

                await Task.WhenAll(
                        RevokeRefreshTokenAsync(refreshTokenData.Token, userId),
                        StoreRefreshTokenAsync(userId, newRefreshToken, RefreshTokenSlidingExpiration))
                    .ConfigureAwait(false);

                return TokenPairResult.Success(newAccessToken, newRefreshToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return TokenPairResult.Fail(AuthErrorType.SystemError);
            }
        }*/

    private static string HashToken(string token)
    {
        // 原始 token 先做哈希，再参与缓存键拼接。
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string TokenKey(string userId, string refreshToken)
    {
        // 用户维度 + 哈希 token 组成最终缓存键，方便精确撤销单张票据。
        return $"{RefreshTokenPrefix}{userId}:{HashToken(refreshToken)}";
    }

    private async Task<RefreshToken?> GetRefreshToken(string userId, string refreshToken)
    {
        return await _cacheProvider.GetAsync<RefreshToken>(TokenKey(userId, refreshToken));
    }

    /// <summary>
    /// 验证并撤销刷新令牌
    /// </summary>
    /// <param name="userId">用户的唯一标识符</param>
    /// <param name="refreshToken">要验证和撤销的刷新令牌</param>
    /// <returns>如果刷新令牌有效并且成功撤销，则返回true；否则返回false</returns>
    public async Task<bool> ValidateAndRevokeRefreshTokenAsync(string userId, string refreshToken)
    {
        // 先校验旧票仍然有效，再马上回收，避免同一张刷新令牌重复使用。
        var refreshTokenData = await GetRefreshToken(userId, refreshToken);
        if (refreshTokenData is null || refreshTokenData.ExpiresAt < DateTime.UtcNow) return false;

        // 再校验是否由同一设备发起，避免 refresh token 被跨设备滥用。
        var deviceId = _deviceInfo.GetDeviceId();
        if (deviceId is null || refreshTokenData.DeviceId != deviceId) return false;

        // 验证通过后，立刻销毁旧的（轮换机制）
        await RevokeRefreshTokenAsync(userId, refreshToken);
        return true;
    }


    /// <summary>
    /// 轮换刷新令牌，撤销旧令牌并存储新令牌
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="oldRefreshToken"></param>
    /// <param name="newRefreshToken"></param>
    /// <returns></returns>
    /// <returns></returns>
    public async Task RotateRefreshTokenAsync(string userId, string oldRefreshToken, string newRefreshToken)
    {
        // 先撤销旧票，再写入新票，轮换语义会更直观，也更容易排查问题。
        await RevokeRefreshTokenAsync(userId, oldRefreshToken);
        await StoreRefreshTokenAsync(userId, newRefreshToken);
    }

    /// <summary>
    /// 登录时颁发访问令牌和刷新令牌，并将刷新令牌存储在服务器端（如 Redis）以供后续验证和轮换使用
    /// </summary>
    /// <param name="user"></param>
    /// <param name="roles"></param>
    /// <returns></returns>
    public async Task<TokenIssueResult> IssueLoginTokensAsync(ApplicationUser user, IList<string> roles)
    {
        // 两类令牌的生命周期不同，分别计算，方便前后端各自使用。
        var now = DateTime.UtcNow;
        var accessTokenExpiresAtUtc = now.AddMinutes(_settings.AccessTokenExpirationMinutes);
        var refreshTokenExpiresAtUtc = now.AddDays(_settings.RefreshTokenExpirationDays);

        // 访问令牌负责鉴权，刷新令牌负责续签，两者总是成对下发。
        var accessToken = GenerateAccessToken(user, roles);
        var refreshToken = GenerateRefreshToken();

        // 刷新令牌必须先落服务端，后续刷新和撤销都依赖这份服务端状态。
        await StoreRefreshTokenAsync(user.Id.ToString(), refreshToken);

        // 返回访问令牌和刷新令牌以及它们的过期时间
        return new TokenIssueResult
        {
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc
        };
    }
}
