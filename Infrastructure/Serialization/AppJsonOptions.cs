using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Serialization;

/// <summary>
/// 全局统一 JSON 序列化选项。
/// <list type="bullet">
///   <item>HTTP 控制器响应（通过 <see cref="ApplyTo"/>）</item>
///   <item>Redis 缓存（<see cref="TextJsonSerializer"/>）</item>
///   <item>源生成上下文（<see cref="AppJsonContext"/>）</item>
/// </list>
/// 所有序列化行为应从此处修改，无需分散在多个地方。
/// </summary>
public static class AppJsonOptions
{
    /// <summary>
    /// 应用级默认 <see cref="JsonSerializerOptions"/>，只读单例，避免重复分配。
    /// </summary>
    public static readonly JsonSerializerOptions Default = CreateDefault();

    private static JsonSerializerOptions CreateDefault()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling         = JsonCommentHandling.Skip,
            ReferenceHandler            = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        };

        // 优先走源生成，未覆盖的类型再回退反射。
        options.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
        return options;
    }

    /// <summary>
    /// 将 <see cref="Default"/> 中的所有选项应用到 <paramref name="target"/>。
    /// <para>
    /// ASP.NET Core 的 <c>AddJsonOptions</c> 回调不允许替换选项实例，只能修改属性，
    /// 因此提供此辅助方法保证控制器序列化与应用其余部分保持一致。
    /// </para>
    /// </summary>
    public static void ApplyTo(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy        = Default.PropertyNamingPolicy;
        target.PropertyNameCaseInsensitive = Default.PropertyNameCaseInsensitive;
        target.Encoder                     = Default.Encoder;
        target.ReadCommentHandling         = Default.ReadCommentHandling;
        target.ReferenceHandler            = Default.ReferenceHandler;
        target.DefaultIgnoreCondition      = Default.DefaultIgnoreCondition;
        target.TypeInfoResolverChain.Clear();
        foreach (var resolver in Default.TypeInfoResolverChain)
            target.TypeInfoResolverChain.Add(resolver);
    }
}
