namespace Core.Interfaces;

// 序列化接口
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(byte[] bytes);

}