using Core.Interfaces;

namespace Infrastructure.Services;

/// <summary>
/// 默认内容扫描：危险扩展名 / PE·ELF 魔数拒绝；其余放行。
/// 可替换为真实 AV 实现。
/// </summary>
public sealed class DenyListAttachmentContentScanner : IAttachmentContentScanner
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".msi", ".msp",
        ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".apk", ".jar", ".sh", ".bash", ".zsh", ".elf",
    };

    public Task<AttachmentContentScanResult> ScanAsync(
        Stream content,
        string? sniffedContentType,
        string? originalName,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(originalName))
        {
            var ext = Path.GetExtension(originalName.Trim());
            if (!string.IsNullOrEmpty(ext) && DangerousExtensions.Contains(ext))
                return Task.FromResult(AttachmentContentScanResult.Deny($"危险扩展名被拒绝: {ext}"));
        }

        // 读前 4 字节做 PE/ELF 拒绝（调用方应保证可 Seek，或已定位开头）
        Span<byte> header = stackalloc byte[4];
        var read = 0;
        if (content.CanSeek)
        {
            var pos = content.Position;
            try
            {
                content.Position = 0;
                while (read < 4)
                {
                    var n = content.Read(header[read..]);
                    if (n == 0) break;
                    read += n;
                }
            }
            finally
            {
                content.Position = pos;
            }
        }

        if (read >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
            return Task.FromResult(AttachmentContentScanResult.Deny("检测到 PE/MZ 可执行文件头"));
        if (read >= 4
            && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            return Task.FromResult(AttachmentContentScanResult.Deny("检测到 ELF 可执行文件头"));

        return Task.FromResult(AttachmentContentScanResult.Allow());
    }
}
