namespace Core.Caching;

/// <summary>
/// 缓存 Key 构建工具，统一管理前缀拼接规则，方便跨项目复用。
/// </summary>
public static class CacheKeyBuilder
{
    /// <summary>
    /// 拼接前缀与业务 key，生成完整的 Redis key。
    /// </summary>
    /// <param name="prefix">命名空间前缀，如 "chat:user:"</param>
    /// <param name="key">业务 key</param>
    public static string WithPrefix(string prefix, string key)
        => string.Concat(prefix, key);

    /// <summary>
    /// 根据完整 key 生成对应的分布式锁 key。
    /// </summary>
    public static string LockKey(string fullKey)
        => string.Concat(CacheConstants.LockKeyPrefix, fullKey);

    /// <summary>
    /// 构建带二级命名空间的 key，格式：{prefix}{domain}:{id}
    /// </summary>
    public static string WithDomain(string prefix, string domain, string id)
        => string.Concat(prefix, domain, ":", id);
}
