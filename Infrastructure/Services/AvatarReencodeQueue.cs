namespace Infrastructure.Services;

/// <summary>有界头像重编码闸门：限制并发，避免请求峰值打满 CPU。</summary>
public sealed class AvatarReencodeQueue
{
    private readonly SemaphoreSlim _workers;

    public AvatarReencodeQueue(int maxConcurrency = 2)
    {
        var n = Math.Max(1, maxConcurrency);
        _workers = new SemaphoreSlim(n, n);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workers.Release();
        }
    }
}
