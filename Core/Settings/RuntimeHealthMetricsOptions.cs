namespace Core.Settings;

/// <summary>
/// Controls active diagnostic probes.  Passive gauges remain available to
/// /debug/metrics; the Redis probe is disabled for isolated performance runs
/// so its periodic command is not attributed to the API workload.
/// </summary>
public sealed class RuntimeHealthMetricsOptions
{
    public const string SectionName = "RuntimeHealthMetrics";

    public bool RedisPingEnabled { get; set; } = true;

    public int RedisPingIntervalSeconds { get; set; } = 30;
}
