using ChatApp.Server.Models.Calls;
using ChatApp.Shared.Protocol.Tcp;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Calls;

/// <summary>
/// 服务端 call grant 签名器（HMAC-SHA256）单元测试。
/// 验证签名确定性、载荷篡改检测，以及 canonical 载荷与 Realtime/Gateway 校验端一致。
/// GROUP-CALL-1：群组 grant 的 HMAC 覆盖全部参与者（canonical 载荷由 Shared
/// <see cref="TcpCallGrantSignature"/> 权威定义，Direct 与 0.5.6 双人格式逐字节一致）。
/// </summary>
public sealed class CallGrantSignerTests
{
    private const string Secret = "call-grant-signing-test-secret-32bytes";

    private static CallGrantResponse Grant(long caller = 1001, long callee = 1002)
        => new()
        {
            CallId = "call-abc",
            CallerUserId = caller,
            CalleeUserId = callee,
            ExpiresAtMs = 1_700_000_060_000,
            Nonce = "nonce-test",
            Signature = string.Empty
        };

    private static CallGrantResponse GroupGrant() => new()
    {
        CallId = "call-group-1",
        CallerUserId = 42,
        CalleeUserId = 0,
        ExpiresAtMs = 1_700_000_060_000,
        Nonce = "nonce-group-1",
        CallKind = CallGrantContracts.CallKindGroup,
        ParticipantUserIds = [42, 43, 44],
        Signature = string.Empty
    };

    [Fact]
    public void Sign_Produces_Deterministic_Signature()
    {
        var a = CallGrantSigner.Sign(Secret, Grant());
        var b = CallGrantSigner.Sign(Secret, Grant());
        Assert.Equal(a, b);
        Assert.False(string.IsNullOrWhiteSpace(a));
    }

    [Fact]
    public void Sign_Changes_When_Field_Mutates()
    {
        var baseSignature = CallGrantSigner.Sign(Secret, Grant());
        var tampered = Grant();
        tampered.CalleeUserId = 1003;
        var mutatedSignature = CallGrantSigner.Sign(Secret, tampered);
        Assert.NotEqual(baseSignature, mutatedSignature);
    }

    [Fact]
    public void Sign_Changes_When_Nonce_Mutates()
    {
        var baseSignature = CallGrantSigner.Sign(Secret, Grant());
        var tampered = Grant();
        tampered.Nonce = "nonce-other";
        Assert.NotEqual(baseSignature, CallGrantSigner.Sign(Secret, tampered));
    }

    [Fact]
    public void CanonicalPayload_Is_Stable_And_Ordered()
    {
        var grant = Grant();
        var payload = CallGrantSigner.BuildCanonicalPayload(grant);
        Assert.Equal($"call-abc|1001|1002|1700000060000|nonce-test", payload);
    }

    [Fact]
    public void Sign_With_Empty_Secret_Throws()
    {
        Assert.Throws<ArgumentException>(() => CallGrantSigner.Sign("", Grant()));
    }

    // ---- GROUP-CALL-1：多人 grant ----

    [Fact]
    public void GroupCanonicalPayload_Covers_All_Participants()
    {
        var payload = CallGrantSigner.BuildCanonicalPayload(GroupGrant());

        // 与 Shared TcpCallGrantSignature 权威格式逐字节一致。
        Assert.Equal(
            "call-group-1|42|0|1700000060000|nonce-group-1|G|42,43,44",
            payload);
    }

    [Fact]
    public void GroupSignature_Changes_When_Participant_List_Mutates()
    {
        var baseSignature = CallGrantSigner.Sign(Secret, GroupGrant());

        var replaced = GroupGrant();
        replaced.ParticipantUserIds = [42, 43, 45];
        Assert.NotEqual(baseSignature, CallGrantSigner.Sign(Secret, replaced));

        var dropped = GroupGrant();
        dropped.ParticipantUserIds = [42, 43];
        Assert.NotEqual(baseSignature, CallGrantSigner.Sign(Secret, dropped));
    }

    [Fact]
    public void GroupGrant_RoundTrips_Through_Shared_Verifier()
    {
        var grant = GroupGrant();
        grant.Signature = CallGrantSigner.Sign(Secret, grant);

        // Gateway 群组中继用同一规范载荷与密钥校验。
        Assert.True(TcpCallGrantSignature.TryVerify(
            CallGrantSigner.ToWireGrant(grant), Secret, 1_700_000_000_000, out var errorCode));
        Assert.Null(errorCode);
    }

    [Fact]
    public void GroupGrant_WireCallee_Is_Zero_FailClosedOnLegacyVerifier()
    {
        var grant = GroupGrant();
        grant.Signature = CallGrantSigner.Sign(Secret, grant);

        var wire = CallGrantSigner.ToWireGrant(grant);

        // 旧双人校验端要求 CalleeUserId > 0 → 群组 grant（恒为 0）fail-closed，绝不误入 1:1 状态机。
        Assert.Equal(0, wire.CalleeUserId);
    }
}