using System.Buffers;
using System.IO.Compression;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic content policy used as a baseline and as a pre-filter before
/// an external AV engine. It is deliberately not marketed as malware scanning.
/// </summary>
public sealed class DenyListAttachmentContentScanner(
    IOptions<AttachmentStorageOptions>? options = null) : IAttachmentContentScanner
{
    private static readonly byte[] PdfJavaScriptToken = "/JavaScript"u8.ToArray();
    private static readonly byte[] PdfOpenActionToken = "/OpenAction"u8.ToArray();
    private static readonly byte[] PdfAdditionalActionsToken = "/AA"u8.ToArray();

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".msi", ".msp",
        ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".apk", ".jar", ".sh", ".bash", ".zsh", ".elf",
        ".docm", ".dotm", ".xlsm", ".xlam", ".pptm", ".ppam", ".sldm",
    };

    private readonly AttachmentStorageOptions _options = options?.Value ?? new AttachmentStorageOptions();

    public async Task<AttachmentContentScanResult> ScanAsync(
        Stream content,
        string? sniffedContentType,
        string? originalName,
        CancellationToken cancellationToken = default)
    {
        const string engine = "ChatApp.ContentPolicy";
        const string version = "1";

        if (!string.IsNullOrWhiteSpace(originalName))
        {
            var ext = Path.GetExtension(originalName.Trim());
            if (!string.IsNullOrEmpty(ext) && DangerousExtensions.Contains(ext))
                return AttachmentContentScanResult.Deny(
                    $"危险扩展名被拒绝: {ext}", engine, version);
        }

        if (!content.CanSeek)
            return AttachmentContentScanResult.TransientFail(
                "内容流不可回退，无法完成安全策略扫描", engine, version);

        var originalPosition = content.Position;
        try
        {
            content.Position = 0;
            var header = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var read = 0;
            try
            {
                while (read < 4)
                {
                    var n = await content.ReadAsync(
                            header.AsMemory(read, 4 - read), cancellationToken)
                        .ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }

                if (read >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
                    return AttachmentContentScanResult.Deny(
                        "检测到 PE/MZ 可执行文件头", engine, version);
                if (read >= 4
                    && header[0] == 0x7F && header[1] == (byte)'E'
                    && header[2] == (byte)'L' && header[3] == (byte)'F')
                    return AttachmentContentScanResult.Deny(
                        "检测到 ELF 可执行文件头", engine, version);
                if (read >= 2 && header[0] == (byte)'#' && header[1] == (byte)'!')
                    return AttachmentContentScanResult.Deny(
                        "检测到脚本 shebang", engine, version);

                content.Position = 0;
                if (string.Equals(sniffedContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (await ContainsAnyPdfDangerousTokenAsync(
                                content, header, cancellationToken)
                            .ConfigureAwait(false))
                        return AttachmentContentScanResult.Deny(
                            "PDF 包含主动脚本或自动动作", engine, version);
                }

                if (read >= 2 && header[0] == (byte)'P' && header[1] == (byte)'K')
                {
                    try
                    {
                        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
                        var entries = 0;
                        long expanded = 0;
                        foreach (var entry in archive.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            entries++;
                            expanded = checked(expanded + Math.Max(0, entry.Length));
                            var name = entry.FullName.AsSpan();
                            var depth = CountPathSeparators(name);
                            if (entries > _options.ArchiveMaxEntries
                                || expanded > _options.ArchiveMaxUncompressedBytes
                                || depth > _options.ArchiveMaxPathDepth)
                                return AttachmentContentScanResult.Deny(
                                    "压缩归档超过条目、解压大小或层级限制", engine, version);

                            if (IsScriptOrExecutableEntry(name))
                                return AttachmentContentScanResult.Deny(
                                    "压缩归档包含脚本或可执行内容", engine, version);
                            if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith("/customUI/customUI.xml", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith("\\customUI\\customUI.xml", StringComparison.OrdinalIgnoreCase))
                                return AttachmentContentScanResult.Deny(
                                    "Office 宏或自定义脚本内容被拒绝", engine, version);

                            if (IsNestedArchiveName(name))
                                return AttachmentContentScanResult.Deny(
                                    "嵌套压缩归档被拒绝", engine, version);
                        }
                    }
                    catch (InvalidDataException)
                    {
                        return AttachmentContentScanResult.Deny(
                            "压缩归档结构无效", engine, version);
                    }
                }

                return AttachmentContentScanResult.Allow(engine, version);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }
        }
        finally
        {
            content.Position = originalPosition;
        }
    }

    private static bool IsNestedArchiveName(ReadOnlySpan<char> name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptOrExecutableEntry(ReadOnlySpan<char> name)
    {
        var extensionStart = name.LastIndexOf('.') + 1;
        if (extensionStart > 0 && IsDangerousExtension(name[extensionStart..]))
            return true;

        return name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountPathSeparators(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (character is '/' or '\\')
                count++;
        }

        return count;
    }

    private static bool IsDangerousExtension(ReadOnlySpan<char> extension)
        => extension.Equals("exe", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("dll", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("bat", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("cmd", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("com", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("scr", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("msi", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("msp", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("ps1", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("vbs", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("js", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("jse", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("wsf", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("wsh", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("hta", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("apk", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("jar", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("sh", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("bash", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("zsh", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("elf", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("docm", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("dotm", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("xlsm", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("xlam", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("pptm", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("ppam", StringComparison.OrdinalIgnoreCase)
           || extension.Equals("sldm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One pass over a PDF. The three tokens are short, non-overlapping
    /// patterns, so a small state machine preserves matches across read
    /// boundaries without allocating a combined carry array per chunk.
    /// </summary>
    private static async Task<bool> ContainsAnyPdfDangerousTokenAsync(
        Stream content,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var javascriptMatched = 0;
        var openActionMatched = 0;
        var additionalActionsMatched = 0;
        while (true)
        {
            var read = await content.ReadAsync(
                    buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return false;

            unsafe
            {
                fixed (byte* input = buffer)
                fixed (byte* javascript = PdfJavaScriptToken)
                fixed (byte* openAction = PdfOpenActionToken)
                fixed (byte* additionalActions = PdfAdditionalActionsToken)
                {
                    var current = input;
                    var end = input + read;
                    while (current < end)
                    {
                        var value = *current++;
                        javascriptMatched = AdvancePdfToken(
                            javascript, PdfJavaScriptToken.Length, javascriptMatched, value);
                        openActionMatched = AdvancePdfToken(
                            openAction, PdfOpenActionToken.Length, openActionMatched, value);
                        additionalActionsMatched = AdvancePdfToken(
                            additionalActions, PdfAdditionalActionsToken.Length,
                            additionalActionsMatched, value);

                        if (javascriptMatched == PdfJavaScriptToken.Length
                            || openActionMatched == PdfOpenActionToken.Length
                            || additionalActionsMatched == PdfAdditionalActionsToken.Length)
                            return true;
                    }
                }
            }
        }
    }

    private static unsafe int AdvancePdfToken(
        byte* token,
        int tokenLength,
        int matched,
        byte value)
    {
        if (matched < tokenLength && value == token[matched])
            return matched + 1;

        // All three policy tokens begin with '/' and have no non-trivial
        // prefix/suffix overlap. This reset still preserves a slash that is
        // the first byte of a token at the current position.
        return value == *token ? 1 : 0;
    }
}
