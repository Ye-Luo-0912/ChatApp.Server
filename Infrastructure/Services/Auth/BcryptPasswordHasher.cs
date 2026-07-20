using Core.Interfaces;
using System.Diagnostics;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 使用 BCrypt 实现密码哈希和验证（work factor = 10），并带进程内并发闸门。
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 10;
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _acquireTimeout;

    public BcryptPasswordHasher(IOptions<PasswordHashingOptions>? options = null)
    {
        var opts = options?.Value ?? new PasswordHashingOptions();
        var n = Math.Max(1, opts.MaxConcurrentOperations);
        _gate = new SemaphoreSlim(n, n);
        _acquireTimeout = TimeSpan.FromMilliseconds(Math.Max(0, opts.AcquireTimeoutMilliseconds));
    }

    public string HashPassword(string password)
    {
        if (!TryEnter())
            throw new PasswordVerifyOverloadedException();
        try
        {
            return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (!TryEnter())
            throw new PasswordVerifyOverloadedException();
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryEnter()
    {
        if (_acquireTimeout <= TimeSpan.Zero)
        {
            _gate.Wait();
            return true;
        }

        return _gate.Wait(_acquireTimeout);
    }
}

/// <summary>BCrypt 闸门过载：调用方应快速失败，避免拖垮线程池。</summary>
public sealed class PasswordVerifyOverloadedException : Exception
{
    public PasswordVerifyOverloadedException()
        : base("密码校验过载，请稍后重试")
    {
    }
}
