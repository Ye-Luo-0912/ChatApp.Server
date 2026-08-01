using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Moderation;

namespace Core.Interfaces;

public interface IModerationService
{
    Task<AuthOperationResult> ReportAsync(
        long reporterId,
        UserReportTargetType targetType,
        long? targetUserId,
        string? targetMessageId,
        string reason,
        string? detail,
        CancellationToken cancellationToken = default);

    Task<CursorPage<UserReportDto>> ListReportsAsync(
        UserReportStatus? status,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> ReviewReportAsync(
        long adminId,
        long reportId,
        UserReportStatus newStatus,
        DateTimeOffset? banUntil,
        string? note,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> AppealAsync(
        long userId,
        long reportId,
        string appealNote,
        CancellationToken cancellationToken = default);
}

public sealed class UserReportDto
{
    public long Id { get; init; }
    public long ReporterId { get; init; }
    public UserReportTargetType TargetType { get; init; }
    public long? TargetUserId { get; init; }
    public string? TargetMessageId { get; init; }
    public string Reason { get; init; } = "";
    public string? Detail { get; init; }
    public UserReportStatus Status { get; init; }
    public string? AppealNote { get; init; }
    public DateTimeOffset? BanUntil { get; init; }
    public long? ReviewedByAdminId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface IAccountLifecycleService
{
    Task<AuthOperationResult> ScheduleDeletionAsync(long userId, CancellationToken cancellationToken = default);

    Task<AuthOperationResult> ScheduleDeletionByAdminAsync(
        long userId,
        long actorUserId,
        string? reason,
        string? clientIp,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> CancelDeletionAsync(long userId, CancellationToken cancellationToken = default);

    Task<UserDataExportDto?> ExportAsync(long userId, CancellationToken cancellationToken = default);

    Task<int> ProcessDueDeletionsAsync(CancellationToken cancellationToken = default);
}

public sealed class UserDataExportDto
{
    public long UserId { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string? Signature { get; init; }
    public string? Region { get; init; }
    public DateTimeOffset CreatedDate { get; init; }
    public IReadOnlyList<object> SecurityEvents { get; init; } = [];
    public IReadOnlyList<object> FriendIds { get; init; } = [];
}

public interface INotificationQuery
{
    Task<CursorPage<InAppNotificationDto>> ListAsync(
        long userId, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task MarkReadAsync(long userId, long notificationId, CancellationToken cancellationToken = default);

    Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken = default);

    Task<int> MarkReadBatchAsync(long userId, IReadOnlyList<long> ids, CancellationToken cancellationToken = default);
}

public sealed class InAppNotificationDto
{
    public long Id { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsRead { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
