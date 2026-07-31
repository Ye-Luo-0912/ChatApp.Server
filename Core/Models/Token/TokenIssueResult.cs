namespace Core.Models.Token
{
    public class TokenIssueResult
    {
        public string AccessToken { get; init; } = string.Empty;
        public DateTime AccessTokenExpiresAtUtc { get; init; }

        public string RefreshToken { get; init; } = string.Empty;
        public DateTime RefreshTokenExpiresAtUtc { get; init; }

        /// <summary>本次登录的会话 ID；客户端可用于多设备管理 UI 及 TCP 握手标识。</summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// 设备指纹的 64 位哈希（由服务端计算并直接下发）。
        /// 客户端将此值原样携带至 TCP 握手，TCP 侧与 AccessTokenData.DeviceIdHash 做整数比对，无需重新计算。
        /// 完整设备信息可通过 SessionId 关联的 SessionRecord 获取。
        /// </summary>
        public ulong? DeviceIdHash { get; init; }

        /// <summary>登录时签发的设备凭据明文；仅返回一次，服务端不持久化明文。</summary>
        public string? DeviceCredential { get; init; }
    }
}
