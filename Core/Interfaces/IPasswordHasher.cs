namespace Core.Interfaces;

/// <summary>
/// 密码哈希与验证。
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// 对原始密码进行哈希。
    /// </summary>
    string HashPassword(string password);

    /// <summary>
    /// 验证原始密码与已存储的哈希是否匹配。
    /// </summary>
    bool VerifyPassword(string password, string passwordHash);
}
