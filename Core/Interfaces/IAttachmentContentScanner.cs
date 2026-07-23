namespace Core.Interfaces;

/// <summary>
/// 附件内容扫描钩子（魔数嗅探后的可选恶意软件/危险类型检查）。
/// 默认实现仅做扩展名/魔数拒绝列表；可替换为真实 AV。
/// </summary>
public interface IAttachmentContentScanner
{
    Task<AttachmentContentScanResult> ScanAsync(
        Stream content,
        string? sniffedContentType,
        string? originalName,
        CancellationToken cancellationToken = default);
}

public sealed record AttachmentContentScanResult(bool Allowed, string? Reason = null)
{
    public static AttachmentContentScanResult Allow() => new(true);
    public static AttachmentContentScanResult Deny(string reason) => new(false, reason);
}
