using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Auth;

internal static partial class TokenServiceLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "刷新令牌及会话已写入缓存，UserId={UserId}, DeviceId={DeviceId}")]
    public static partial void RefreshTokenStored(ILogger logger, string userId, string deviceId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "刷新令牌已轮换，UserId={UserId}, DeviceId={DeviceId}")]
    public static partial void RefreshTokenRotated(ILogger logger, string userId, string deviceId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "登录令牌已建立，UserId={UserId}, SessionId={SessionId}, DeviceId={DeviceId}")]
    public static partial void LoginTokensIssued(ILogger logger, string userId, string sessionId, string deviceId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug,
        Message = "令牌轮换失败（无效或已被并发消费），UserId={UserId}")]
    public static partial void TokenRotationFailed(ILogger logger, string userId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug,
        Message = "令牌已轮换，UserId={UserId}, DeviceId={DeviceId}")]
    public static partial void TokenRotated(ILogger logger, string userId, string deviceId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug,
        Message = "会话已撤销，UserId={UserId}, DeviceId={DeviceId}")]
    public static partial void SessionRevoked(ILogger logger, string userId, string deviceId);
}
