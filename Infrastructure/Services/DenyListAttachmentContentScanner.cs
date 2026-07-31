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
            return AttachmentContentScanResult.Allow(engine, version);

        var originalPosition = content.Position;
        try
        {
            content.Position = 0;
            var header = new byte[4];
            var read = 0;
            while (read < header.Length)
            {
                var n = await content.ReadAsync(
                        header.AsMemory(read, header.Length - read), cancellationToken)
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
                foreach (var token in new[] { "/JavaScript"u8.ToArray(), "/OpenAction"u8.ToArray(), "/AA"u8.ToArray() })
                {
                    if (await ContainsTokenAsync(content, token, cancellationToken).ConfigureAwait(false))
                        return AttachmentContentScanResult.Deny(
                            "PDF 包含主动脚本或自动动作", engine, version);
                    content.Position = 0;
                }
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
                        var depth = entry.FullName.Count(c => c == '/' || c == '\\');
                        if (entries > _options.ArchiveMaxEntries
                            || expanded > _options.ArchiveMaxUncompressedBytes
                            || depth > _options.ArchiveMaxPathDepth)
                            return AttachmentContentScanResult.Deny(
                                "压缩归档超过条目、解压大小或层级限制", engine, version);

                        var name = entry.FullName.Replace('\\', '/');
                        if (IsScriptOrExecutableEntry(name))
                            return AttachmentContentScanResult.Deny(
                                "压缩归档包含脚本或可执行内容", engine, version);
                        if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith("/customUI/customUI.xml", StringComparison.OrdinalIgnoreCase))
                            return AttachmentContentScanResult.Deny(
                                "Office 宏或自定义脚本内容被拒绝", engine, version);

                        if (IsNestedArchiveName(name))
                        {
                            var nested = InspectNestedArchive(
                                entry,
                                depth: 1,
                                ref entries,
                                ref expanded,
                                cancellationToken);
                            if (!nested.Allowed)
                                return AttachmentContentScanResult.Deny(
                                    nested.Reason ?? "压缩归档层数超过限制", engine, version);
                        }
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
            content.Position = originalPosition;
        }
    }

    private (bool Allowed, string? Reason) InspectNestedArchive(
        ZipArchiveEntry entry,
        int depth,
        ref int entries,
        ref long expanded,
        CancellationToken cancellationToken)
    {
        if (depth >= _options.ArchiveMaxNestingDepth)
            return (false, "压缩归档层数超过限制");

        try
        {
            using var nestedStream = entry.Open();
            using var nestedArchive = new ZipArchive(nestedStream, ZipArchiveMode.Read);
            foreach (var nestedEntry in nestedArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries++;
                expanded = checked(expanded + Math.Max(0, nestedEntry.Length));
                var name = nestedEntry.FullName.Replace('\\', '/');
                var pathDepth = name.Count(c => c == '/' || c == '\\');
                if (entries > _options.ArchiveMaxEntries
                    || expanded > _options.ArchiveMaxUncompressedBytes
                    || pathDepth > _options.ArchiveMaxPathDepth)
                    return (false, "压缩归档超过条目、解压大小或层级限制");

                if (IsScriptOrExecutableEntry(name))
                    return (false, "压缩归档包含脚本或可执行内容");

                if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("/customUI/customUI.xml", StringComparison.OrdinalIgnoreCase))
                    return (false, "Office 宏或自定义脚本内容被拒绝");

                if (IsNestedArchiveName(name))
                {
                    var deeper = InspectNestedArchive(
                        nestedEntry,
                        depth + 1,
                        ref entries,
                        ref expanded,
                        cancellationToken);
                    if (!deeper.Allowed)
                        return deeper;
                }
            }

            return (true, null);
        }
        catch (InvalidDataException)
        {
            return (false, "嵌套压缩归档结构无效");
        }
    }

    private static bool IsNestedArchiveName(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptOrExecutableEntry(string name)
    {
        var extension = Path.GetExtension(name);
        return DangerousExtensions.Contains(extension)
               || name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ContainsTokenAsync(
        Stream content,
        ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var carry = Array.Empty<byte>();
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;

            var combined = new byte[carry.Length + read];
            carry.CopyTo(combined, 0);
            Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);
            if (combined.AsSpan().IndexOf(token.Span) >= 0)
                return true;

            var keep = Math.Min(token.Length - 1, combined.Length);
            carry = combined[^keep..];
        }
    }
}
