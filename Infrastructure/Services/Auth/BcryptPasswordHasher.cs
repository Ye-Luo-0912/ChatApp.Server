using System.Diagnostics;
using Core.Interfaces;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 使用 BCrypt 实现密码哈希和验证（work factor = 10），异步有界闸门（共享 <see cref="IAuthCpuLimiter"/>）。
/// </summary>
public sealed class BcryptPasswordHasher(IAuthCpuLimiter cpuLimiter) : IPasswordHasher
{
    private const int WorkFactor = 10;

    public int CurrentHashVersion => 1;

    public async Task<string> HashPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        await cpuLimiter.EnterAsync("hash", cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            cpuLimiter.Exit("hash", sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<bool> VerifyPasswordAsync(
        string password, string passwordHash, CancellationToken cancellationToken = default)
    {
        await cpuLimiter.EnterAsync("verify", cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() => BCrypt.Net.BCrypt.Verify(password, passwordHash), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            cpuLimiter.Exit("verify", sw.Elapsed.TotalMilliseconds);
        }
    }

    public bool NeedsRehash(string passwordHash, int storedVersion)
    {
        if (storedVersion < CurrentHashVersion || string.IsNullOrWhiteSpace(passwordHash))
            return true;

        // BCrypt encodes its work factor as the third '$'-delimited part.
        // Unknown or malformed formats are upgrade candidates after the
        // verifier has accepted them.
        var parts = passwordHash.Split('$');
        return parts.Length < 4
               || !int.TryParse(parts[2], out var rounds)
               || rounds < WorkFactor;
    }
}
