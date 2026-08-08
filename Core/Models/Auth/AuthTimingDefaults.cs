namespace Core.Models.Auth;

/// <summary>由 API 返回给客户端的安全流程默认时限。</summary>
public static class AuthTimingDefaults
{
    public static readonly TimeSpan StepUpLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccountDeletionCooldown = TimeSpan.FromDays(14);
}
