namespace Core.Interfaces;

/// <summary>Application boundary for recording administrator actions.</summary>
public interface IAdminAuditWriter
{
    Task WriteAsync(
        long adminUserId,
        long? targetUserId,
        string action,
        string? reason,
        string? detail,
        string? clientIp,
        CancellationToken cancellationToken = default);
}
