namespace Core.Interfaces;

/// <summary>
/// 附件内容扫描钩子（魔数嗅探后的危险类型、归档与恶意软件检查）。
/// DenyList 是开发/预过滤策略；生产通过组合器接入真实 AV。
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
    bool IsTransient = false,
    string? EngineName = null,
    string? EngineVersion = null)
{
    public static AttachmentContentScanResult Allow(
        string? engineName = null,
        string? engineVersion = null) =>
        new(true, EngineName: engineName, EngineVersion: engineVersion);
    public static AttachmentContentScanResult Deny(
        string reason,
        string? engineName = null,
        string? engineVersion = null) =>
        new(false, reason, EngineName: engineName, EngineVersion: engineVersion);
    public static AttachmentContentScanResult TransientFail(
        string reason,
        string? engineName = null,
        string? engineVersion = null) =>
        new(false, reason, IsTransient: true, EngineName: engineName, EngineVersion: engineVersion);
}

