using Amazon.S3;
using Amazon.S3.Model;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Multi-instance export blob store. Objects are streamed directly to/from S3;
/// the bucket's SSE-S3/SSE-KMS policy is applied to every write.
/// </summary>
public sealed class S3DataExportBlobStore : IDataExportBlobStore, IObjectStoreHealthProbe, IDisposable
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

    public async Task<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(
                    _bucket,
                    NormalizeKey(objectKey),
                    cancellationToken)
                .ConfigureAwait(false);
            return response.ResponseStream;
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
}
