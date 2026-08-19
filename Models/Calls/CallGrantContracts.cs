using System.ComponentModel.DataAnnotations;

namespace ChatApp.Server.Models.Calls;

/// <summary>call grant 请求/响应 DTO（Server 签发，Client 消费）。字段名与 camelCase wire 一致。</summary>
public static class CallGrantContracts
{
    /// <summary>call grant 最长有效期（秒）。短生命周期，授权输入有界。</summary>
    public const int MaxGrantLifetimeSeconds = 60;
}

/// <summary>签发 call grant 的失败分类。字符串值即 wire 值，生产端原样输出，消费端按表匹配。</summary>
public static class CallGrantErrorCode
{
    public const string InvalidTargetUser = "call_grant_invalid_target_user";
    public const string NotFriends = "call_grant_not_friends";
    public const string Blocked = "call_grant_blocked";
    public const string SigningUnavailable = "call_grant_signing_unavailable";
}

/// <summary>签发 call grant 的请求（被叫为登录用户以外的目标）。</summary>
public sealed class CallGrantRequest
{
    [Range(1, long.MaxValue)]
    public long CalleeUserId { get; set; }
}

/// <summary>Server 签发的短期 call grant。</summary>
public sealed class CallGrantResponse
{
    public string CallId { get; set; } = string.Empty;
    public long CallerUserId { get; set; }
    public long CalleeUserId { get; set; }
    public long ExpiresAtMs { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}