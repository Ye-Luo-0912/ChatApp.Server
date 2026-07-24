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

/// <param name="Allowed">通过。</param>
/// <param name="Reason">拒绝或瞬时失败原因。</param>
/// <param name="IsTransient">
/// true：瞬时失败（超时/过载/抛错），应保持 Scanning 并退避重试；
/// false：永久拒绝（拒绝列表等），→ Rejected 且不重试。
/// </param>
public sealed record AttachmentContentScanResult(
    bool Allowed,
    string? Reason = null,
    bool IsTransient = false)
{
    public static AttachmentContentScanResult Allow() => new(true);
    public static AttachmentContentScanResult Deny(string reason) => new(false, reason);
    public static AttachmentContentScanResult TransientFail(string reason) =>
        new(false, reason, IsTransient: true);
}
