namespace Infrastructure.Caching;

/// <summary>
/// Garnet（Redis 兼容）缓存配置，从 appsettings.json 的 "GarnetCache" 节读取。
/// </summary>
public sealed class RedisCacheOptions
{
    /// <summary>对应 appsettings.json 的配置节名称。</summary>
    public const string SectionName = "GarnetCache";

    /// <summary>Key 命名空间前缀，默认 "cache:"。</summary>
    public string KeyPrefix { get; set; } = "cache:";

}
