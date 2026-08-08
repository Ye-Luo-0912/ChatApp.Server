using System.Buffers;
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
    ILogger<ClamAvAttachmentContentScanner> logger) : IAttachmentContentScanner, IAttachmentScannerHealthProbe
{
    private const int ChunkSize = 64 * 1024;
    private const int ResponseLimit = 4096;
    private const int ProbeResponseLimit = 32;
    private static readonly byte[] PingCommand = "zPING\0"u8.ToArray();
    private static readonly byte[] PongResponse = "PONG"u8.ToArray();
    private static readonly byte[] InStreamCommand = "zINSTREAM\0"u8.ToArray();
    private static readonly byte[] EndOfStreamChunk = new byte[sizeof(int)];

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ClamAvHost))
            throw new InvalidOperationException("ClamAV host 未配置");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1_000, opts.ClamAvTimeoutMilliseconds));
        using var client = new TcpClient();
        await client.ConnectAsync(opts.ClamAvHost, opts.ClamAvPort, timeout.Token)
            .ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(PingCommand, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

        var response = ArrayPool<byte>.Shared.Rent(ProbeResponseLimit);
        try
        {
            var length = 0;
            while (length < ProbeResponseLimit)
            {
                var read = await stream.ReadAsync(
                        response.AsMemory(length, ProbeResponseLimit - length),
                        timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                length += read;
                if (response.AsSpan(0, length).Contains((byte)'\n'))
                    break;
            }

            var trimmed = TrimAscii(response.AsSpan(0, length));
            if (trimmed.Length < PongResponse.Length
                || !trimmed[..PongResponse.Length].SequenceEqual(PongResponse))
                throw new InvalidOperationException("ClamAV readiness probe 未收到 PONG");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(response);
        }
    }

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
            await stream.WriteAsync(InStreamCommand, ct).ConfigureAwait(false);

            if (content.CanSeek)
                content.Position = 0;

            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + sizeof(int));
            try
            {
                while (true)
                {
                    var read = await content.ReadAsync(
                            buffer.AsMemory(sizeof(int), ChunkSize), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, sizeof(int)), read);
                    await stream.WriteAsync(buffer.AsMemory(0, sizeof(int)), ct).ConfigureAwait(false);
                    await stream.WriteAsync(
                            buffer.AsMemory(sizeof(int), read), ct)
                        .ConfigureAwait(false);
                }

                await stream.WriteAsync(EndOfStreamChunk, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                var responseLength = 0;
                while (responseLength < ResponseLimit)
                {
                    var read = await stream.ReadAsync(
                            buffer.AsMemory(responseLength, ResponseLimit - responseLength), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    var responseChunk = buffer.AsSpan(responseLength, read);
                    responseLength += read;
                    if (responseChunk.Contains((byte)'\n'))
                        break;
                }

                var verdict = ParseVerdict(buffer.AsSpan(0, responseLength));
                if (verdict == ClamVerdict.Allowed)
                    return AttachmentContentScanResult.Allow("ClamAV", opts.ClamAvEngineVersion);

                // The common OK path above stays entirely inside the pooled
                // buffer. Build a diagnostic string only for a reject or an
                // unexpected response; these are not the throughput path.
                var responseText = Encoding.UTF8.GetString(buffer, 0, responseLength).Trim();
                if (verdict == ClamVerdict.Found)
                    return AttachmentContentScanResult.Deny(
                        $"ClamAV 拒绝: {responseText}", "ClamAV", opts.ClamAvEngineVersion);

                return AttachmentContentScanResult.TransientFail(
                    $"ClamAV 返回未知结果: {responseText}", engine, opts.ClamAvEngineVersion);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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

    private enum ClamVerdict : byte
    {
        Unknown,
        Allowed,
        Found,
    }

    private static ClamVerdict ParseVerdict(ReadOnlySpan<byte> response)
    {
        response = TrimAscii(response);
        if (ContainsAsciiIgnoreCase(response, "FOUND"u8))
            return ClamVerdict.Found;

        return response.Length >= 2 && response[^2..].SequenceEqual("OK"u8)
            ? ClamVerdict.Allowed
            : ClamVerdict.Unknown;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
            start++;

        var end = value.Length;
        while (end > start && (IsAsciiWhitespace(value[end - 1]) || value[end - 1] == 0))
            end--;

        return value[start..end];
    }

    private static bool ContainsAsciiIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
            return true;
        if (value.Length < needle.Length)
            return false;

        for (var i = 0; i <= value.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (ToUpperAscii(value[i + j]) != ToUpperAscii(needle[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }

    private static byte ToUpperAscii(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
            ? (byte)(value - ('a' - 'A'))
            : value;

    private static bool IsAsciiWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
