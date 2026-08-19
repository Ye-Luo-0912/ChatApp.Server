using ChatApp.Server.Models.Calls;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Calls;

/// <summary>
/// 服务端 call grant 签名器（HMAC-SHA256）单元测试。
/// 验证签名确定性、载荷篡改检测，以及 canonical 载荷与 Realtime 校验端一致。
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
}