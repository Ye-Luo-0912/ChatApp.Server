using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ChatApp.Server.Models.Calls;

/// <summary>call grant 请求/响应 DTO（Server 签发，Client 消费）。字段名与 camelCase wire 一致。</summary>
public static class CallGrantContracts
{
    /// <summary>call grant 最长有效期（秒）。短生命周期，授权输入有界。</summary>
    public const int MaxGrantLifetimeSeconds = 60;

    /// <summary>
    /// 群组通话（Mesh 阶段一）grant 名单人数上限（含主叫）。
    /// 与 Shared <c>TcpCallConstants.MaxGroupCallParticipants</c> 一致。
    /// </summary>
    public const int MaxGroupCallParticipants = 4;

    /// <summary>CallKind wire 值：1:1 双人通话（缺省）。</summary>
    public const string CallKindDirect = "direct";

    /// <summary>CallKind wire 值：多人通话（Mesh ≤4 人）。</summary>
    public const string CallKindGroup = "group";
}

/// <summary>签发 call grant 的失败分类。字符串值即 wire 值，生产端原样输出，消费端按表匹配。</summary>
public static class CallGrantErrorCode
{
    public const string InvalidTargetUser = "call_grant_invalid_target_user";
    public const string NotFriends = "call_grant_not_friends";
    public const string Blocked = "call_grant_blocked";
    public const string SigningUnavailable = "call_grant_signing_unavailable";

    /// <summary>群组参与者名单非法（为空 / 超上限 / 重复 / 含自己 / 非法 Id / 与 CallKind 组合矛盾）。</summary>
    public const string InvalidParticipants = "call_grant_invalid_participants";

    /// <summary>未知 callKind（仅支持 direct / group）。</summary>
    public const string InvalidCallKind = "call_grant_invalid_call_kind";
}

/// <summary>
/// 签发 call grant 的请求（被叫为登录用户以外的目标）。
/// <para>
/// 群组通话（Mesh 阶段一）：<c>callKind="group"</c> + <c>participantUserIds</c>（被邀请人列表，
/// 不含主叫，1..MaxGroupCallParticipants-1 人）。旧请求（不带这两个字段）= 双人通话，行为不变。
/// </para>
/// </summary>
public sealed class CallGrantRequest
{
    [Range(1, long.MaxValue)]
    public long CalleeUserId { get; set; }

    /// <summary>通话种类：null/"direct" = 双人（缺省）；"group" = 群组。</summary>
    public string? CallKind { get; set; }

    /// <summary>群组被邀请人列表（不含主叫）。仅 callKind="group" 时允许携带。</summary>
    public IReadOnlyList<long>? ParticipantUserIds { get; set; }
}

/// <summary>Server 签发的短期 call grant。</summary>
public sealed class CallGrantResponse
{
    public string CallId { get; set; } = string.Empty;
    public long CallerUserId { get; set; }

    /// <summary>被叫用户 Id。群组通话恒为 0（成员以 participantUserIds 为准）。</summary>
    public long CalleeUserId { get; set; }
    public long ExpiresAtMs { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;

    /// <summary>通话种类：双人通话为 null（省略，兼容旧响应）；群组为 "group"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallKind { get; set; }

    /// <summary>群组完整成员名单（升序、含主叫）。双人通话为 null（省略）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<long>? ParticipantUserIds { get; set; }
}