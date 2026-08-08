using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Process-local byte reservation for export staging. Directory-size checks
/// are still kept as a crash-recovery guard, but they cannot prevent two
/// workers from passing the same check concurrently. This budget is the
/// admission boundary for live export work.
/// </summary>
public sealed class DataExportStagingBudget : IDisposable
{
    private readonly long _maxBytes;
    private readonly string _stagingRoot;
    private readonly ILogger<DataExportStagingBudget> _logger;
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _waiter;
    private long _reservedBytes;
    private bool _disposed;

    public DataExportStagingBudget(
        IOptions<DataExportStorageOptions> options,
        ILogger<DataExportStagingBudget> logger)
    {
        var value = options.Value;
        _maxBytes = Math.Max(1, value.StagingMaxBytes);
        _stagingRoot = Path.GetFullPath(
            Path.Combine(
                string.IsNullOrWhiteSpace(value.LocalRootPath)
                    ? "App_Data/exports"
                    : value.LocalRootPath,
                ".staging"));
        Directory.CreateDirectory(_stagingRoot);
        _logger = logger;
    }

    public long MaxBytes => _maxBytes;

    public long CurrentReservedBytes => Interlocked.Read(ref _reservedBytes);

    public long CurrentBytes => Interlocked.Read(ref _actualBytes);

    private long _actualBytes;

    public async ValueTask<Lease> ReserveAsync(
        long requestedBytes,
        CancellationToken cancellationToken = default)
    {
        requestedBytes = Math.Max(1, requestedBytes);
        if (requestedBytes > _maxBytes)
            throw new InvalidOperationException("导出 staging 单作业预留超过磁盘配额");

        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _actualBytes = ReadActualBytes();
                AuthSecurityMetrics.SetExportStagingBytes(_actualBytes);
                if (_actualBytes > _maxBytes)
                    throw new InvalidOperationException("导出 staging 磁盘配额已耗尽");
                var accountedBytes = Math.Max(_reservedBytes, _actualBytes);
                if (accountedBytes <= _maxBytes - requestedBytes)
                {
                    _reservedBytes += requestedBytes;
                    AuthSecurityMetrics.SetExportStagingReservedBytes(_reservedBytes);
                    return new Lease(this, requestedBytes);
                }

                _waiter ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _waiter.Task;
            }

            _logger.LogDebug(
                "导出 staging 等待字节预算 Requested={RequestedBytes} Reserved={ReservedBytes} Max={MaxBytes}",
                requestedBytes,
                CurrentReservedBytes,
                _maxBytes);
            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _reservedBytes = Math.Max(0, _reservedBytes - bytes);
            AuthSecurityMetrics.SetExportStagingReservedBytes(_reservedBytes);
            _waiter?.TrySetResult(true);
            _waiter = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _reservedBytes = 0;
            AuthSecurityMetrics.SetExportStagingReservedBytes(0);
            _waiter?.TrySetException(new ObjectDisposedException(nameof(DataExportStagingBudget)));
            _waiter = null;
        }
    }

    private long ReadActualBytes()
    {
        long bytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _stagingRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                try { bytes = checked(bytes + new FileInfo(path).Length); }
                catch (FileNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return bytes;
    }

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        private readonly DataExportStagingBudget _owner;
        private readonly long _bytes;
        private int _released;

        internal Lease(DataExportStagingBudget owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public long Bytes => _bytes;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(_bytes);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
