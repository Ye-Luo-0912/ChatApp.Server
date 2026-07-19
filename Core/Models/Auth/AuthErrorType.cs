namespace Core.Models.Auth;

public enum AuthErrorType : byte
{
    /// <summary>
    /// 无效的认证凭据
    /// </summary>
    InvalidCredentials,
    /// <summary>
    /// 访问令牌已过期
    /// </summary>
    TokenExpired,
    /// <summary>
    /// 设备标识不匹配
    /// </summary>
    DeviceMismatch,
    /// <summary>
    /// 系统错误
    /// </summary>
    SystemError
}