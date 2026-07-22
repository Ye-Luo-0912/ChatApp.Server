using System.Diagnostics;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 使用 BCrypt 实现密码哈希和验证（work factor = 10），异步有界闸门。
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

    public async Task<string> HashPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        await EnterAsync("hash", cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            AuthSecurityMetrics.EndPasswordOp("hash", sw.Elapsed.TotalMilliseconds);
            _gate.Release();
        }
    }

    public async Task<bool> VerifyPasswordAsync(
        string password, string passwordHash, CancellationToken cancellationToken = default)
    {
        await EnterAsync("verify", cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() => BCrypt.Net.BCrypt.Verify(password, passwordHash), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            AuthSecurityMetrics.EndPasswordOp("verify", sw.Elapsed.TotalMilliseconds);
            _gate.Release();
        }
    }

    private async Task EnterAsync(string op, CancellationToken cancellationToken)
    {
        var waitSw = Stopwatch.StartNew();
        bool acquired;
        if (_acquireTimeout <= TimeSpan.Zero)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
        }
        else
        {
            acquired = await _gate.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false);
        }

        AuthSecurityMetrics.RecordPasswordWait(op, waitSw.Elapsed.TotalMilliseconds);

        if (!acquired)
        {
            AuthSecurityMetrics.RecordPasswordOverloaded(op);
            throw new Core.Exceptions.PasswordVerifyOverloadedException();
        }

        AuthSecurityMetrics.BeginPasswordOp();
    }
}
