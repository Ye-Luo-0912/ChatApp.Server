namespace Infrastructure.Diagnostics;

/// <summary>统一的指数退避 + jitter，避免多个 Worker 在同一时刻重试形成尖峰。</summary>
public static class LeasedJobBackoff
{
    public static TimeSpan ExponentialWithJitter(
        TimeSpan baseDelay,
        int attempt,
        TimeSpan maximum)
    {
        var baseSeconds = Math.Max(0.001, baseDelay.TotalSeconds);
        var exponent = Math.Min(Math.Max(0, attempt - 1), 10);
        var exponential = Math.Min(maximum.TotalSeconds, baseSeconds * Math.Pow(2, exponent));
        var jittered = exponential * (0.8 + Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(Math.Min(maximum.TotalSeconds, jittered));
    }
}
