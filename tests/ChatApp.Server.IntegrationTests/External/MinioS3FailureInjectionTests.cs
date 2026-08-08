using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.External;

public sealed class MinioS3FailureInjectionTests
{
    [SkippableFact]
    [Trait("Category", "MinIO")]
    public async Task ExportBlobStore_FailsDuringOutage_AndRecoversAfterRestart()
    {
        var endpoint = Environment.GetEnvironmentVariable("CHATAPP_TEST_MINIO_ENDPOINT");
        var bucket = Environment.GetEnvironmentVariable("CHATAPP_TEST_MINIO_BUCKET");
        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        Skip.If(string.IsNullOrWhiteSpace(endpoint)
                || string.IsNullOrWhiteSpace(bucket)
                || string.IsNullOrWhiteSpace(accessKey)
                || string.IsNullOrWhiteSpace(secretKey),
            "MinIO endpoint, bucket, and AWS credentials are required for this gate.");
        Skip.If(!string.Equals(
                Environment.GetEnvironmentVariable("CHATAPP_TEST_MINIO_DOCKER_RESTART"),
                "1", StringComparison.Ordinal),
            "Set CHATAPP_TEST_MINIO_DOCKER_RESTART=1 to run the Docker fault-injection gate.");

        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", accessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", secretKey);

        using var admin = new AmazonS3Client(
            accessKey,
            secretKey,
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                SignatureVersion = "4",
            });
        await EnsureBucketAsync(admin, bucket);

        var options = Options.Create(new DataExportStorageOptions
        {
            Provider = "S3",
            S3Bucket = bucket,
            S3Endpoint = endpoint,
            S3Region = "us-east-1",
            S3ForcePathStyle = true,
            S3SseMode = "SSE-S3",
        });
        using var store = new S3DataExportBlobStore(
            options, NullLogger<S3DataExportBlobStore>.Instance);

        var key = $"candidates/integration-{Guid.NewGuid():N}.json";
        var expected = "minio-fault-injection-payload"u8.ToArray();
        try
        {
            await store.WriteAsync(key, new MemoryStream(expected));
            await AssertPayloadAsync(store, key, expected);

            await RunDockerAsync("stop", "chatapp_minio");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await Assert.ThrowsAnyAsync<Exception>(
                    () => store.OpenReadAsync(key, timeout.Token));
            }
            finally
            {
                await RunDockerAsync("start", "chatapp_minio");
                await WaitForMinioAsync(admin, bucket);
            }

            await AssertPayloadAsync(store, key, expected);
        }
        finally
        {
            try
            {
                await store.DeleteAsync(key);
            }
            catch
            {
                // The gate reports the original failure; cleanup is best effort.
            }
        }
    }

    private static async Task EnsureBucketAsync(IAmazonS3 client, string bucket)
    {
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.Conflict
            || string.Equals(ex.ErrorCode, "BucketAlreadyOwnedByYou", StringComparison.Ordinal))
        {
            // The CI bucket is shared by this test only; creation is idempotent.
        }
    }

    private static async Task AssertPayloadAsync(
        S3DataExportBlobStore store,
        string key,
        byte[] expected)
    {
        await using var stream = await store.OpenReadAsync(key)
            ?? throw new InvalidOperationException("MinIO object was not readable.");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Assert.Equal(expected, memory.ToArray());
    }

    private static async Task WaitForMinioAsync(IAmazonS3 client, string bucket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            try
            {
                await client.GetBucketLocationAsync(
                    new GetBucketLocationRequest { BucketName = bucket }, timeout.Token);
                return;
            }
            catch (Exception) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(500, timeout.Token);
            }
        }
    }

    private static async Task RunDockerAsync(string command, string container)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { command, container },
        }) ?? throw new InvalidOperationException("docker could not be started");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"docker {command} {container} failed: {error}");
        }
    }
}
