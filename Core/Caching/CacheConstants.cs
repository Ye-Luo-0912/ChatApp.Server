namespace Core.Caching;

/// <summary>
/// 缓存基础常量，供所有需要读写缓存的项目共享引用，避免魔法字符串散落各处。
/// </summary>
public static class CacheConstants
{
    // ── Hash 字段名 ──────────────────────────────────────────────
    public const string ValueField             = "value";
    public const string AbsoluteExpirationField = "absExp";
    public const string SlidingExpirationField  = "slidExp";

    // ── 空值穿透防护标记 ─────────────────────────────────────────
    /// <summary>
    /// 存入该标记表示来源数据确实不存在，防止频繁回源。
    /// </summary>
    public const string NullValueMarker = "__NULL__";

    // ── 分布式锁前缀 ─────────────────────────────────────────────
    public const string LockKeyPrefix = "lock:";

    // ── MFA 登录挑战 ─────────────────────────────────────────────
    public const string MfaPendingPrefix = "mfa:pending:";
    public const string MfaAttemptsPrefix = "mfa:attempts:";

    // ── 敏感操作 step-up（v2 绑定 userId+session+device+purpose+nonce）──
    public const string StepUpPrefix = "stepup:token:";
    /// <summary>key 后缀为 <c>{userId}:{sessionId}:{deviceHash}</c>。</summary>
    public const string RecentMfaPrefix = "stepup:recent-mfa:";

    /// <summary>TOTP 已用时间步：<c>totp:used:{userId}:{timestep}</c>，TTL ~3 分钟防重放。</summary>
    public const string TotpUsedPrefix = "totp:used:";

    /// <summary>附件鉴权下载短时票：<c>attachment:download:{ticket}</c>，单次消费。</summary>
    public const string AttachmentDownloadTicketPrefix = "attachment:download:";
}