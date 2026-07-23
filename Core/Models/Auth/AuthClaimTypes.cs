namespace Core.Models.Auth;

/// <summary>不透明访问令牌写入 <see cref="System.Security.Claims.ClaimsPrincipal"/> 的自定义声明。</summary>
public static class AuthClaimTypes
{
    public const string SessionId = "sid";
    public const string DeviceIdHash = "didh";
}
