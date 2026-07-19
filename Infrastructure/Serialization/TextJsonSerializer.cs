using Core.Interfaces;

namespace Infrastructure.Serialization;

/// <summary>
/// 基于 <see cref="System.Text.Json"/> 的序列化实现，用于 Redis 等内部缓存层。
/// 序列化选项由 <see cref="AppJsonOptions.Default"/> 统一提供。
/// </summary>
public class TextJsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, AppJsonOptions.Default);

    public T? Deserialize<T>(byte[] bytes)
        => System.Text.Json.JsonSerializer.Deserialize<T>(bytes, AppJsonOptions.Default);
}