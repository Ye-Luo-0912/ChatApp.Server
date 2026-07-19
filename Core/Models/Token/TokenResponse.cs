namespace Core.Models.Token
{
    public class TokenResponse
    {
        /// <summary>
        /// 访问令牌
        /// </summary>
        public required string AccessToken { get; set; }

        /// <summary>
        /// 访问令牌过期时间（秒）
        /// </summary>
        public int ExpiresIn { get; set; }

        /// <summary>
        /// 刷新令牌（仅登录时返回）
        /// </summary>
        public string? RefreshToken { get; set; }

        /// <summary>
        /// 关联设备ID（用于后续刷新令牌）
        /// </summary>
        public string? DeviceId { get; set; }
    }
}