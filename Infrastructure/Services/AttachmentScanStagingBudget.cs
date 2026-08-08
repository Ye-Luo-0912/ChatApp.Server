using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Process-local byte budget for scan staging. The Worker derives its claim
/// concurrency from the same budget, and each scan reserves the full maximum
/// object size before opening the remote stream. This bounds tmpfs use even if
/// a client declared a smaller size than the object actually contains.
/// </summary>
public sealed class AttachmentScanStagingBudget : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private readonly long _maxConcurrentBytes;
    private readonly long _maxStagingBytes;
    private readonly string _root;
    private readonly ILogger<AttachmentScanStagingBudget> _logger;
    private long _reservedBytes;
    private bool _disposed;

    public AttachmentScanStagingBudget(
        IOptions<AttachmentStorageOptions> options,
        ILogger<AttachmentScanStagingBudget> logger)
    {
        var value = options.Value;
        // ScanMaxConcurrentBytes limits active work; ScanStagingMaxBytes and
        // TmpfsSizeBytes limit the physical staging boundary. Keep both
        // constraints here as a runtime guard even when options validation is
        // bypassed by a test or a manually-created scope.
        _maxConcurrentBytes = Math.Max(1, value.ScanMaxConcurrentBytes);
        _maxStagingBytes = Math.Max(
            1,
            value.TmpfsSizeBytes > 0
                ? Math.Min(value.ScanStagingMaxBytes, value.TmpfsSizeBytes)
                : value.ScanStagingMaxBytes);
        _root = Path.GetFullPath(value.ScanStagingRoot);
        _logger = logger;
        Directory.CreateDirectory(_root);
        RemoveCrashResidue();
    }

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
                return _reservedBytes;
        }
    }

    public string CreatePath()
        => Path.Combine(_root, $"chatapp-attachment-scan-{Guid.NewGuid():N}.blob");

    public async ValueTask<IAsyncDisposable> ReserveAsync(
        long requestedBytes,
        CancellationToken cancellationToken = default)
    {
        requestedBytes = Math.Max(1, requestedBytes);
        if (requestedBytes > _maxConcurrentBytes || requestedBytes > _maxStagingBytes)
        {
            AuthSecurityMetrics.AttachmentScanStagingRejected();
            throw new InvalidOperationException(
                $"扫描对象大小 {requestedBytes} 超过 staging 字节预算 "
                + $"(concurrent={_maxConcurrentBytes}, total={_maxStagingBytes})");
        }

        while (true)
        {
            TaskCompletionSource<bool>? waiter = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_reservedBytes <= _maxConcurrentBytes - requestedBytes
                    && _reservedBytes <= _maxStagingBytes - requestedBytes)
                {
                    _reservedBytes += requestedBytes;
                    AuthSecurityMetrics.SetAttachmentScanStagingBytes(_reservedBytes);
                    return new Reservation(this, requestedBytes);
                }

                waiter = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }

            try
            {
                await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                waiter.TrySetCanceled();
                throw;
            }
        }
    }

    public void Dispose()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetException(new ObjectDisposedException(nameof(AttachmentScanStagingBudget)));
    }

    private void Release(long bytes)
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (_gate)
        {
            _reservedBytes = Math.Max(0, _reservedBytes - bytes);
            AuthSecurityMetrics.SetAttachmentScanStagingBytes(_reservedBytes);
            while (_waiters.Count > 0)
            {
                var candidate = _waiters.Dequeue();
                if (!candidate.Task.IsCompleted)
                {
                    waiter = candidate;
                    break;
                }
            }
        }

        waiter?.TrySetResult(true);
    }

    private void RemoveCrashResidue()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "chatapp-attachment-scan-*.blob"))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理扫描 staging 残留失败 Path={Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举扫描 staging 残留失败 Root={Root}", _root);
        }
    }

    private sealed class Reservation(
        AttachmentScanStagingBudget owner,
        long bytes) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(bytes);
            return ValueTask.CompletedTask;
        }
    }
}
