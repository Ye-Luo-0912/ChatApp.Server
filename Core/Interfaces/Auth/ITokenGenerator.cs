namespace Core.Interfaces.Auth;

/// <summary>
/// 生成密码学安全的随机令牌字符串。
/// 无任何 IO 依赖，实现可注册为单例。
/// </summary>
public interface ITokenGenerator
{
    /// <summary>
    /// 生成一个 URL 安全的随机令牌字符串。
    /// </summary>
    /// <param name="byteLength">随机字节数，默认 32（256 位熵）。</param>
    string Generate(int byteLength = 32);
}
