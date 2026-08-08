using System.Text;

namespace Core.Interfaces;

// 序列化接口
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(byte[] bytes);

    /// <summary>
    /// Deserializes JSON that is already held as text. Text-native serializers
    /// can override this to avoid a transient UTF-8 byte[]; the default keeps
    /// older implementations source-compatible.
    /// </summary>
    T? Deserialize<T>(string json)
        => Deserialize<T>(Encoding.UTF8.GetBytes(json));

}
