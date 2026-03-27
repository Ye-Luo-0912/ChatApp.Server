using Core.Interfaces;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Serializer;

// 默认JSON实现
public class TextJsonSerializer(JsonSerializerOptions? options = null) : ISerializer
{
    
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,          // 属性名不区分大小写
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 允许不安全的字符（例如中文）
        ReadCommentHandling = JsonCommentHandling.Skip,       // 忽略 JSON 注释
        ReferenceHandler = ReferenceHandler.IgnoreCycles,    // 忽略循环引用 (重要！)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // 忽略 null 值 (可选)
    };

    public byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    public T? Deserialize<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes,_options);
    }
}