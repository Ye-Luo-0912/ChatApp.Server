namespace Core.Interfaces;

/// <summary>
/// 共享的 Auth CPU 有界闸门（密码 BCrypt、遗留恢复码 BCrypt 等），防止同步 CPU 热路径打满。
/// </summary>
public interface IAuthCpuLimiter
{
    /// <summary>进入闸门；超时抛出 <see cref="Core.Exceptions.PasswordVerifyOverloadedException"/>。</summary>
    Task EnterAsync(string op, CancellationToken cancellationToken = default);

    /// <summary>离开闸门并记录耗时。</summary>
    void Exit(string op, double elapsedMilliseconds);
}
