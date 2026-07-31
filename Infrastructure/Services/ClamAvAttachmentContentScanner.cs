using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>ClamAV INSTREAM adapter. Network/timeout errors are transient.</summary>
public sealed class ClamAvAttachmentContentScanner(
    IOptions<AttachmentStorageOptions> options,
    ILogger<ClamAvAttachmentContentScanner> logger) : IAttachmentContentScanner
{
    public async Task<AttachmentContentScanResult> ScanAsync(
        Stream content,
        string? sniffedContentType,
        string? originalName,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        const string engine = "ClamAV";
        if (string.IsNullOrWhiteSpace(opts.ClamAvHost))
            return AttachmentContentScanResult.TransientFail(
                "ClamAV host 未配置", engine, opts.ClamAvEngineVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1_000, opts.ClamAvTimeoutMilliseconds));
        var ct = timeout.Token;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(opts.ClamAvHost, opts.ClamAvPort, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), ct).ConfigureAwait(false);

            if (content.CanSeek)
                content.Position = 0;

            var buffer = new byte[64 * 1024];
            var length = new byte[4];
            while (true)
            {
                var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                BinaryPrimitives.WriteInt32BigEndian(length, read);
                await stream.WriteAsync(length, ct).ConfigureAwait(false);
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await stream.WriteAsync(new byte[4], ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            using var response = new MemoryStream();
            var responseBuffer = new byte[256];
            while (true)
            {
                var read = await stream.ReadAsync(responseBuffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                await response.WriteAsync(responseBuffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (responseBuffer.AsSpan(0, read).Contains((byte)'\n')) break;
                if (response.Length > 4096) break;
            }

            var verdict = Encoding.UTF8.GetString(response.ToArray()).Trim();
            if (verdict.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                return AttachmentContentScanResult.Deny(
                    $"ClamAV 拒绝: {verdict}", "ClamAV", opts.ClamAvEngineVersion);
            if (verdict.EndsWith("OK", StringComparison.OrdinalIgnoreCase))
                return AttachmentContentScanResult.Allow("ClamAV", opts.ClamAvEngineVersion);

            return AttachmentContentScanResult.TransientFail(
                $"ClamAV 返回未知结果: {verdict}", engine, opts.ClamAvEngineVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AttachmentContentScanResult.TransientFail(
                "ClamAV 扫描超时", engine, opts.ClamAvEngineVersion);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            logger.LogWarning(ex, "ClamAV 扫描服务不可用");
            return AttachmentContentScanResult.TransientFail(
                "ClamAV 服务不可用", engine, opts.ClamAvEngineVersion);
        }
    }
}
