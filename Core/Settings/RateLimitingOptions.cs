namespace Core.Settings;

/// <summary>限流策略可配置项；Performance 环境可显著放宽以便容量压测。</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int AuthLoginPermitLimit { get; set; } = 10;
    public int AuthLoginWindowSeconds { get; set; } = 60;

    public int AuthRefreshPermitLimit { get; set; } = 30;
    public int AuthRefreshWindowSeconds { get; set; } = 60;

    public int AuthEmailPermitLimit { get; set; } = 5;
    public int AuthEmailWindowSeconds { get; set; } = 60;

    public int UserEmailChangePermitLimit { get; set; } = 3;
    public int UserEmailChangeWindowSeconds { get; set; } = 900;
}
