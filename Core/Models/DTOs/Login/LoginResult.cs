namespace Core.Models.DTOs.Login;

public sealed class LoginResult
{
    public  bool IsSuccess { get; init; }
    
    public long?  UserId { get; init; }
    
    public string? UserName { get; init; }
    
    public  ServerEndPoint? Server { get; init; }
    public  string? AccessToken { get; init; }
    public  DateTime AccessTokenExpiresAtUtc { get; init; }
    public  string? RefreshToken { get; init; }
    public  DateTime RefreshTokenExpiresAtUtc { get; init; }
    public  string? ErrorMessage { get; init; }

    public  LoginCheckStatus LoginCheckStatus { get; init; }

    /// <summary>
    /// 登录失败结果工厂方法，创建一个表示登录失败的LoginResult实例，包含错误信息和登录检查状态。
    /// </summary>
    /// <param name="error"></param>
    /// <param name="status"></param>
    /// <returns></returns>
    public static LoginResult Fail(string error,  LoginCheckStatus status) => new()
    {
        IsSuccess = false,
        ErrorMessage = error,
        LoginCheckStatus = status
    };

    /// <summary>
    /// 登录成功结果工厂方法，创建一个表示登录成功的LoginResult实例，包含用户ID、用户名、访问令牌及其过期时间、刷新令牌及其过期时间和服务端点信息。
    /// </summary>
    /// <param name="userId">用户的唯一标识符。</param>
    /// <param name="userName">用户的名称。</param>
    /// <param name="accessToken">用于后续请求的身份验证的访问令牌。</param>
    /// <param name="accessTokenExpiresAtUtc">访问令牌的过期UTC时间。</param>
    /// <param name="refreshToken">用于刷新访问令牌的刷新令牌。</param>
    /// <param name="refreshTokenExpiresAtUtc">刷新令牌的过期UTC时间。</param>
    /// <param name="server">提供服务的服务器端点信息。</param>
    /// <returns>返回一个表示登录成功的LoginResult对象。</returns>
    public static LoginResult Success(
        long userId,
        string? userName,
        string accessToken,
        DateTime accessTokenExpiresAtUtc,
        string refreshToken,
        DateTime refreshTokenExpiresAtUtc,
        ref ServerEndPoint server
    ) => new()
    {
        IsSuccess = true, 
        UserId = userId,
        UserName = userName,
        AccessToken = accessToken,
        AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
        RefreshToken = refreshToken,
        RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc,
        Server = server,
        LoginCheckStatus = LoginCheckStatus.Success
    };
    
}

/// <summary>
/// 登录状态枚举，表示登录验证的结果状态
/// </summary>
public enum LoginCheckStatus:byte
{
    /// <summary>
    /// 表示登录验证成功，用户凭据有效，可以正常登录系统。
    /// </summary>
    Success =   1,
    /// <summary>
    /// 表示由于凭据无效，认证失败。
    /// </summary>
    InvalidCredentials = 2,
    /// <summary>
    /// 表示由于多次失败尝试，账户被锁定。用户需要等待一段时间或联系管理员解锁后才能再次尝试登录。
    /// </summary>
    LockedOut = 3,
    /// <summary>
    /// 表示用户被禁止登录。这可能是由于管理员禁用了账户，或者用户违反了服务条款等原因导致的。被禁止登录的用户无法访问系统，除非管理员重新启用账户。
    /// </summary>
    NotAllowed = 4,
    /// <summary>
    /// 表示用户需要通过两步验证才能登录。这通常发生在用户启用了两步验证但尚未完成验证过程时。
    /// </summary>
    RequiresTwoFactor = 5
}

