using System.Diagnostics;
using Core.Exceptions;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>进程内 SemaphoreSlim 闸门，供密码与遗留恢复码 BCrypt 共用。</summary>
public sealed class AuthCpuLimiter : IAuthCpuLimiter
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _acquireTimeout;

    public AuthCpuLimiter(IOptions<PasswordHashingOptions>? options = null)
    {
        var opts = options?.Value ?? new PasswordHashingOptions();
        var n = Math.Max(1, opts.MaxConcurrentOperations);
        _gate = new SemaphoreSlim(n, n);
        _acquireTimeout = TimeSpan.FromMilliseconds(Math.Max(0, opts.AcquireTimeoutMilliseconds));
    }

    public async Task EnterAsync(string op, CancellationToken cancellationToken = default)
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
            throw new PasswordVerifyOverloadedException();
        }

        AuthSecurityMetrics.BeginPasswordOp();
    }

    public void Exit(string op, double elapsedMilliseconds)
    {
        AuthSecurityMetrics.EndPasswordOp(op, elapsedMilliseconds);
        _gate.Release();
    }
}
