using Core.Models.Attachment;
using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>
/// 写入 Realtime Postgres <c>realtime.attachments</c>（Migration012 契约）。
/// 连接串取自 MessageEvidence / DataExport RealtimeConnectionString。
/// </summary>
public interface IAttachmentMetadataStore
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }

    /// <summary>
    /// 秒传去重候选：上传者已确认/已绑定的内容中，content_hash 匹配的最新一条。
    /// 命中时 Presign 返回 Deduplicated，客户端免上传。列缺失/表不可用返回 null。
    /// </summary>
    Task<AttachmentDedupCandidate?> TryFindDedupCandidateAsync(
        long uploaderUserId,
        string sha256Hex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在用户级串行化边界内检查未确认对象数和总存储字节，并预留 Ticketed 行。
    /// 配额检查与插入必须原子完成。
    /// </summary>
    Task<AttachmentUploadReservationStatus> ReserveTicketedAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName,
        string? clientAttachmentId = null,
        CancellationToken cancellationToken = default);

    Task ConfirmAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName = null,
        CancellationToken cancellationToken = default);

    /// <summary>上传落盘后：Ticketed → Uploaded → Scanning（禁止绑定/下载直至 Confirmed）。</summary>
    Task MarkUploadedScanningAsync(
        string attachmentId,
        long uploaderUserId,
        long sizeBytes,
        string? sha256Hex = null,
        CancellationToken cancellationToken = default);

    /// <summary>扫描失败：→ Rejected。</summary>
    Task MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 鉴权下载：Bound 须为 conversation_members 成员（或消息收发方回退）；
    /// Confirmed 且未绑定仅上传者本人；Uploaded/Scanning 返回 NotReady。
    /// </summary>
    Task<AttachmentDownloadAccess> ResolveDownloadAccessAsync(
        string attachmentId,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>读取上传者自己的生命周期状态，不返回未授权用户的存在性。</summary>
    Task<AttachmentRecord?> GetStatusForUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentRecord?>(null);

    /// <summary>导出优先：用户上传的 Confirmed/Bound，或已绑定到其消息的附件。</summary>
    Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
        long userId,
        int maxRows = 50_000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
        long uploaderUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Confirmed/Bound 的 object_key 集合（孤儿年龄扫描用）。</summary>
    Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(
        CancellationToken cancellationToken = default);

    Task MarkAbandonedAsync(
        IReadOnlyList<string> attachmentIds,
        CancellationToken cancellationToken = default);

    Task MarkAbandonedByUploaderAsync(
        long uploaderUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 上传者放弃未绑定附件（Ticketed/Confirmed 且 message_id 为空）。
    /// 成功返回 object_key；不存在/无权/已绑定返回 null。
    /// </summary>
    Task<string?> TryAbandonUnboundByUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量放弃过期未绑定 Ticketed/Confirmed（message_id 为空），并重复返回
    /// 已 Abandoned 但尚未成功建立 blob 删除墓碑的候选。
    /// </summary>
    Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
        TimeSpan maxAge,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 运维只读：过期未绑定 Confirmed、过期 Ticketed/Uploaded、卡住 Scanning；外加 Active size 提示。
    /// </summary>
    Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
        TimeSpan orphanAge,
        TimeSpan stuckScanningAge,
        int sampleLimit,
        CancellationToken cancellationToken = default);
}

/// <summary>年龄清扫放弃的一条附件元数据。</summary>
public sealed record AttachmentAbandonBatchItem(
    string AttachmentId,
    string ObjectKey,
    long UploaderUserId);

/// <summary>秒传去重命中结果：源对象键与展示信息。</summary>
public sealed record AttachmentDedupCandidate(
    string AttachmentId,
    string ObjectKey,
    string ContentType,
    long SizeBytes);

/// <summary>元数据侧孤儿/卡住扫描聚合（供 Admin ops）。</summary>
public sealed record AttachmentOpsOrphanQueryResult(
    bool Available,
    string? UnavailableReason,
    long ConfirmedUnboundPastAgeCount,
    long AbandonedUploadingPastAgeCount,
    long StuckScanningCount,
    long? OldestConfirmedUnboundAtMs,
    long? OldestUploadingAtMs,
    long? OldestStuckScanningAtMs,
    long ActiveAttachmentCount,
    long ActiveSizeBytesSum,
    IReadOnlyList<AttachmentOpsOrphanSample> WorstConfirmedUnbound,
    IReadOnlyList<AttachmentOpsOrphanSample> WorstUploading,
    IReadOnlyList<AttachmentOpsOrphanSample> WorstStuckScanning);

public sealed record AttachmentOpsOrphanSample(
    string AttachmentId,
    string ObjectKey,
    long UploaderUserId,
    short Status,
    long SizeBytes,
    long CreatedAtMs);

/// <summary>未配置 Realtime 连接串时的空实现。</summary>
public sealed class UnavailableAttachmentMetadataStore : IAttachmentMetadataStore
{
    public static UnavailableAttachmentMetadataStore Instance { get; } = new();

    public bool IsAvailable => false;
    public string UnavailableReason =>
        "未配置 MessageEvidence:RealtimeConnectionString / DataExport:RealtimeConnectionString";

    public Task<AttachmentDedupCandidate?> TryFindDedupCandidateAsync(
        long uploaderUserId,
        string sha256Hex,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentDedupCandidate?>(null);

    public Task<AttachmentUploadReservationStatus> ReserveTicketedAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName,
        string? clientAttachmentId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AttachmentUploadReservationStatus.MetadataUnavailable);

    public Task ConfirmAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkUploadedScanningAsync(
        string attachmentId,
        long uploaderUserId,
        long sizeBytes,
        string? sha256Hex = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<AttachmentDownloadAccess> ResolveDownloadAccessAsync(
        string attachmentId,
        long userId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AttachmentDownloadAccess(
            attachmentId, string.Empty, "application/octet-stream", null,
            AttachmentDownloadDecision.Unavailable));

    public Task<AttachmentRecord?> GetStatusForUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentRecord?>(null);

    public Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
        long userId, int maxRows = 50_000, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AttachmentRecord>>([]);

    public Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
        long uploaderUserId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    public Task MarkAbandonedAsync(
        IReadOnlyList<string> attachmentIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkAbandonedByUploaderAsync(
        long uploaderUserId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<string?> TryAbandonUnboundByUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
        TimeSpan maxAge,
        int batchSize,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AttachmentAbandonBatchItem>>([]);

    public Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
        TimeSpan orphanAge,
        TimeSpan stuckScanningAge,
        int sampleLimit,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AttachmentOpsOrphanQueryResult(
            Available: false,
            UnavailableReason: UnavailableReason,
            ConfirmedUnboundPastAgeCount: 0,
            AbandonedUploadingPastAgeCount: 0,
            StuckScanningCount: 0,
            OldestConfirmedUnboundAtMs: null,
            OldestUploadingAtMs: null,
            OldestStuckScanningAtMs: null,
            ActiveAttachmentCount: 0,
            ActiveSizeBytesSum: 0,
            WorstConfirmedUnbound: [],
            WorstUploading: [],
            WorstStuckScanning: []));
}
