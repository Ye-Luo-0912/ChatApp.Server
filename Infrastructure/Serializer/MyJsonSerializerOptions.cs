using System.Text.Json;

namespace Infrastructure.Serializer;

public static class MyJsonSerializerOptions
{
    public static JsonSerializerOptions DefaultOptions => new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true, // 忽略大小写
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}