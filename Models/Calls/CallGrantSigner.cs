using ChatApp.Shared.Protocol.Tcp;

namespace ChatApp.Server.Models.Calls;

/// <summary>
/// 签发 call grant 的 HMAC-SHA256 签名器。
/// <para>
/// 规范载荷由 Shared <see cref="TcpCallGrantSignature"/> 统一定义（单一权威实现）：
/// Direct 与既有双人格式逐字节一致 <c>CallId|CallerUserId|CalleeUserId|ExpiresAtMs|Nonce</c>；
/// 群组（GROUP-CALL-1）载荷追加 <c>|G|升序参与者列表</c>，HMAC 因此覆盖全部参与者，
/// 且群组 grant（CalleeUserId=0）在旧双人校验端天然 fail-closed。
/// 以 <c>JwtSettings.Secret</c> 为共享密钥，输出标准 Base64。
/// Gateway 群组中继 / Realtime 校验端使用同一 canonical 载荷与同一密钥。
/// </para>
/// </summary>
public static class CallGrantSigner
{
    /// <summary>规范载荷字段分隔符（字段均为受限字符集，不含该分隔符）。</summary>
    internal const char Separator = '|';

    /// <summary>计算 grant 的 HMAC-SHA256 签名（标准 Base64）。</summary>
    public static string Sign(string secret, CallGrantResponse grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(grant);

        return TcpCallGrantSignature.Sign(secret, ToWireGrant(grant));
    }

    /// <summary>构造与 Gateway/Realtime 校验端完全一致的规范载荷。</summary>
    public static string BuildCanonicalPayload(CallGrantResponse grant)
        => TcpCallGrantSignature.BuildCanonicalPayload(ToWireGrant(grant));

    /// <summary>Server DTO → wire grant 映射（CallKind/Participants 参与签名）。</summary>
    internal static TcpCallGrant ToWireGrant(CallGrantResponse grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return new TcpCallGrant
        {
            CallId = grant.CallId,
            CallerUserId = grant.CallerUserId,
            CalleeUserId = grant.CalleeUserId,
            ExpiresAtMs = grant.ExpiresAtMs,
            Nonce = grant.Nonce,
            Signature = grant.Signature,
            CallKind = ParseCallKind(grant.CallKind),
            Participants = grant.ParticipantUserIds
        };
    }

    private static TcpCallKind? ParseCallKind(string? callKind) => callKind switch
    {
        null or "" => null,
        CallGrantContracts.CallKindDirect => TcpCallKind.Direct,
        CallGrantContracts.CallKindGroup => TcpCallKind.Group,
        _ => throw new ArgumentException($"未知 callKind：{callKind}", nameof(callKind)),
    };
}
