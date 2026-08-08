using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>附件运维查询和管理员修复操作的应用边界。</summary>
public interface IAttachmentOpsAdminService
{
    Task<AttachmentOpsOrphansDto> GetOrphansAsync(CancellationToken cancellationToken = default);

    Task<AttachmentOpsDeleteFailuresDto> GetDeleteFailuresAsync(CancellationToken cancellationToken = default);

    Task<AttachmentOpsScanBacklogDto> GetScanBacklogAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentScanAuditDto>> GetScanAuditsAsync(
        string attachmentId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<AttachmentOpsHintsDto> GetHintsAsync(CancellationToken cancellationToken = default);

    Task<bool> RescanAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);
}
