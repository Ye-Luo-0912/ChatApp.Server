using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Infrastructure.Services;

/// <summary>
/// S3 client and server-side encryption construction shared by all object stores.
/// The AWS SDK default credential chain is intentional: production should use an
/// IAM role, workload identity, or the standard environment/profile providers.
/// </summary>
internal static class S3ClientFactory
{
    public static AmazonS3Client Create(
        string? region,
        string? endpoint,
        bool forcePathStyle)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region ?? "us-east-1"),
            ForcePathStyle = forcePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(endpoint))
            config.ServiceURL = endpoint;

        return new AmazonS3Client(config);
    }

    public static void ApplyServerSideEncryption(
        PutObjectRequest request,
        string mode,
        string? kmsKeyId)
    {
        switch (NormalizeMode(mode))
        {
            case "SSE-S3":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
            case "SSE-KMS":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
                break;
            default:
                throw new InvalidOperationException($"不支持的 S3 SSE 模式: {mode}");
        }
    }

    public static void ApplyServerSideEncryption(
        GetPreSignedUrlRequest request,
        string mode,
        string? kmsKeyId)
    {
        switch (NormalizeMode(mode))
        {
            case "SSE-S3":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
            case "SSE-KMS":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
                break;
            default:
                throw new InvalidOperationException($"不支持的 S3 SSE 模式: {mode}");
        }
    }

    public static void ApplyServerSideEncryption(
        CopyObjectRequest request,
        string mode,
        string? kmsKeyId)
    {
        switch (NormalizeMode(mode))
        {
            case "SSE-S3":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
            case "SSE-KMS":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
                break;
            default:
                throw new InvalidOperationException($"不支持的 S3 SSE 模式: {mode}");
        }
    }

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, "SSE-KMS", StringComparison.OrdinalIgnoreCase)
            ? "SSE-KMS"
            : "SSE-S3";
}
