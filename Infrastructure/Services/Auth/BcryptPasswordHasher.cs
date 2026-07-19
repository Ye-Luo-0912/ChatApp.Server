using Core.Interfaces;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 使用 BCrypt 实现密码哈希和验证（work factor = 10）。
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 10;

    public string HashPassword(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool VerifyPassword(string password, string passwordHash) =>
        BCrypt.Net.BCrypt.Verify(password, passwordHash);
}
