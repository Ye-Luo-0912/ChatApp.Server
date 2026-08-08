using Amazon.S3;
using Amazon.S3.Model;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Multi-instance export blob store. Objects are streamed directly to/from S3;
/// the bucket's SSE-S3/SSE-KMS policy is applied to every write.
/// </summary>
public sealed class S3DataExportBlobStore : IDataExportBlobStore, IObjectStoreHealthProbe, IS3LifecycleHealthProbe, IDisposable
{
    private readonly string _bucket;
    private readonly DataExportStorageOptions _options;
    private readonly IAmazonS3 _s3;
    private readonly ILogger<S3DataExportBlobStore> _logger;

    public S3DataExportBlobStore(
        IOptions<DataExportStorageOptions> options,
        ILogger<S3DataExportBlobStore> logger)
    {
        _options = options.Value;
        _bucket = _options.S3Bucket
                  ?? throw new InvalidOperationException("DataExport:S3Bucket 未配置");
        _logger = logger;
        _s3 = S3ClientFactory.Create(
            _options.S3Region,
            _options.S3Endpoint,
            _options.S3ForcePathStyle);
    }

    public async Task WriteAsync(
        string objectKey,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("导出对象键不能为空", nameof(objectKey));

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = NormalizeKey(objectKey),
            InputStream = content,
            ContentType = "application/json",
        };
        S3ClientFactory.ApplyServerSideEncryption(
            request,
            _options.S3SseMode,
            _options.S3KmsKeyId);
        await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(
                    _bucket,
                    "__chatapp_healthcheck_nonexistent__",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expected: the probe object is intentionally not present.
        }
    }

    public Task ValidateLifecycleAsync(CancellationToken cancellationToken = default) =>
        S3LifecycleConfigurationValidator.RequireAsync(
            _s3,
            _bucket,
            [S3LifecycleRequirement.Prefix("candidates/")],
            cancellationToken);

    public async Task<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = NormalizeKey(objectKey);
            var metadata = await _s3.GetObjectMetadataAsync(
                    _bucket,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            return new S3RangeReadStream(_s3, _bucket, key, metadata.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return;

        try
        {
            await _s3.DeleteObjectAsync(
                    _bucket,
                    NormalizeKey(objectKey),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 S3 导出对象失败 Key={Key}", objectKey);
            throw;
        }
    }

    private static string NormalizeKey(string objectKey) => objectKey.TrimStart('/');

    public void Dispose() => _s3.Dispose();

    /// <summary>
    /// Seekable, range-backed S3 stream. ASP.NET Core's FileStreamResult can
    /// therefore honor Range/206 without downloading an entire export to API
    /// disk. Each seek opens only the requested 1 MiB S3 byte window.
    /// </summary>
    private sealed class S3RangeReadStream(
        IAmazonS3 s3,
        string bucket,
        string key,
        long length) : Stream
    {
        private const int RangeBytes = 1024 * 1024;
        private GetObjectResponse? _rangeResponse;
        private Stream? _rangeStream;
        private long _rangeStart;
        private long _rangeEnd = -1;
        private long _rangeStreamPosition = -1;
        private long _position;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => CanRead;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ReadCoreAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0 || next > length)
                throw new IOException("S3 导出流位置超出范围");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _position = next;
            return _position;
        }

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        private async ValueTask<int> ReadCoreAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (buffer.IsEmpty || _position >= length)
                return 0;

            await EnsureRangeAsync(cancellationToken).ConfigureAwait(false);
            var available = checked((int)Math.Min(
                buffer.Length,
                _rangeEnd - _position + 1));
            var read = await _rangeStream!.ReadAsync(
                    buffer[..available],
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                await DisposeRangeAsync().ConfigureAwait(false);
                throw new IOException("S3 导出 Range 流提前结束");
            }

            _position += read;
            _rangeStreamPosition += read;
            return read;
        }

        private async ValueTask EnsureRangeAsync(CancellationToken cancellationToken)
        {
            // A seek changes the logical position but cannot move the
            // response stream.  Reusing a response after a backward (or
            // skipped-forward) seek would return bytes from the old stream.
            // Only reuse it when the underlying stream is known to be at the
            // requested position.
            if (_rangeStream is not null
                && _position >= _rangeStart
                && _position <= _rangeEnd
                && _position == _rangeStreamPosition)
                return;

            await DisposeRangeAsync().ConfigureAwait(false);
            var end = Math.Min(length - 1, checked(_position + RangeBytes - 1));
            var response = await s3.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        ByteRange = new ByteRange(_position, end),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                _rangeResponse = response;
                _rangeStream = response.ResponseStream;
                _rangeStart = _position;
                _rangeEnd = end;
                _rangeStreamPosition = _position;
            }
            catch
            {
                response.ResponseStream?.Dispose();
                (response as IDisposable)?.Dispose();
                throw;
            }
        }

        private async ValueTask DisposeRangeAsync()
        {
            var stream = _rangeStream;
            var response = _rangeResponse;
            _rangeStream = null;
            _rangeResponse = null;
            _rangeEnd = -1;
            _rangeStreamPosition = -1;
            if (stream is null && response is null)
                return;

            try
            {
                if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                (response as IDisposable)?.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (disposing)
            {
                var stream = _rangeStream;
                var response = _rangeResponse;
                _rangeStream = null;
                _rangeResponse = null;
                _rangeEnd = -1;
                _rangeStreamPosition = -1;
                stream?.Dispose();
                (response as IDisposable)?.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await DisposeRangeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
