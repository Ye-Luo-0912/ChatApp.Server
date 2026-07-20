using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Moderation;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class ModerationService(
    UserDbContext db,
    ISessionStore sessionStore,
    ISecurityEventStore securityEventStore,
    IMessageEvidenceProvider messageEvidence,
    ILogger<ModerationService> logger) : IModerationService
{
    private static readonly HashSet<UserReportStatus> TerminalStatuses =
        [UserReportStatus.Rejected, UserReportStatus.ActionTaken];

    public async Task<AuthOperationResult> ReportAsync(
        long reporterId,
        UserReportTargetType targetType,
        long? targetUserId,
        string? targetMessageId,
        string reason,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return AuthOperationResult.Fail("ValidationFailed", "举报原因不能为空");
        if (reason.Trim().Length > 200)
            return AuthOperationResult.Fail("ValidationFailed", "举报原因过长");
        if (detail is { Length: > 2000 })
            return AuthOperationResult.Fail("ValidationFailed", "举报详情过长");

        if (targetType == UserReportTargetType.User && (targetUserId is null or <= 0))
            return AuthOperationResult.Fail("ValidationFailed", "目标用户无效");

        if (targetType == UserReportTargetType.Message && string.IsNullOrWhiteSpace(targetMessageId))
            return AuthOperationResult.Fail("ValidationFailed", "目标消息无效");

        if (targetUserId == reporterId)
            return AuthOperationResult.Fail("InvalidTarget", "不能举报自己");

        if (targetUserId is > 0)
        {
            var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId, cancellationToken);
            if (!exists)
                return AuthOperationResult.Fail("TargetNotFound", "目标用户不存在");
        }

        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var duplicate = await db.UserReports.AsNoTracking().AnyAsync(
            r => r.ReporterId == reporterId
                 && r.TargetType == targetType
                 && r.TargetUserId == targetUserId
                 && r.TargetMessageId == targetMessageId
                 && r.CreatedAt >= since
                 && r.Status != UserReportStatus.Rejected,
            cancellationToken);
        if (duplicate)
            return AuthOperationResult.Fail("DuplicateReport", "24 小时内已提交过相同举报");

        string? evidenceSnapshot = null;
        if (targetType == UserReportTargetType.Message)
        {
            var evidence = await messageEvidence.TryGetAsync(targetMessageId!.Trim(), cancellationToken);
            if (evidence is null)
                return AuthOperationResult.Fail("EvidenceUnavailable", "无法从消息服务获取可信证据，请稍后重试");

            if (targetUserId is null or <= 0)
                targetUserId = evidence.SenderUserId;

            evidenceSnapshot = Truncate(JsonSerializer.Serialize(new
            {
                evidence.MessageId,
                evidence.SenderUserId,
                SentAtUtc = evidence.SentAtUtc,
                evidence.ContentHashSha256,
                Body = evidence.BodyText,
                Source = "message-service",
            }), 4000);
        }

        var report = new UserReport
        {
            ReporterId = reporterId,
            TargetType = targetType,
            TargetUserId = targetUserId,
            TargetMessageId = targetMessageId,
            EvidenceSnapshot = evidenceSnapshot,
            Reason = reason.Trim(),
            Detail = detail,
            Status = UserReportStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.UserReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);

        await securityEventStore.RecordAsync(
            reporterId, SecurityEventType.ReportSubmitted,
            detail: $"report={report.Id} type={targetType}",
            cancellationToken: cancellationToken);

        logger.LogInformation("用户 {ReporterId} 提交举报 {ReportId}", reporterId, report.Id);
        return AuthOperationResult.Success();
    }

    public async Task<CursorPage<UserReportDto>> ListReportsAsync(
        UserReportStatus? status,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;

        var query = db.UserReports.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(r => r.Status == status);
        if (cursorId.HasValue) query = query.Where(r => r.Id < cursorId.Value);

        var rows = await query.OrderByDescending(r => r.Id).Take(pageSize + 1)
            .Select(r => new UserReportDto
            {
                Id = r.Id,
                ReporterId = r.ReporterId,
                TargetType = r.TargetType,
                TargetUserId = r.TargetUserId,
                TargetMessageId = r.TargetMessageId,
                Reason = r.Reason,
                Detail = r.Detail,
                Status = r.Status,
                AppealNote = r.AppealNote,
                BanUntil = r.BanUntil,
                ReviewedByAdminId = r.ReviewedByAdminId,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new CursorPage<UserReportDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }

    public async Task<AuthOperationResult> ReviewReportAsync(
        long adminId,
        long reportId,
        UserReportStatus newStatus,
        DateTimeOffset? banUntil,
        string? note,
        CancellationToken cancellationToken = default)
    {
        if (newStatus is not (UserReportStatus.Reviewed or UserReportStatus.ActionTaken or UserReportStatus.Rejected))
            return AuthOperationResult.Fail("InvalidTransition", "审核状态无效");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var report = await db.UserReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
            if (report is null)
                return AuthOperationResult.Fail("NotFound", "举报不存在");

            if (!CanTransition(report.Status, newStatus))
                return AuthOperationResult.Fail("InvalidTransition",
                    $"不能从 {report.Status} 转到 {newStatus}");

            report.Status = newStatus;
            report.ReviewedByAdminId = adminId;
            report.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(note))
                report.Detail = string.IsNullOrWhiteSpace(report.Detail) ? note : $"{report.Detail}\n[admin] {note}";

            if (newStatus == UserReportStatus.ActionTaken && banUntil.HasValue && report.TargetUserId is { } targetId)
            {
                report.BanUntil = banUntil;
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetId, cancellationToken);
                if (user is not null)
                {
                    user.BanUntil = banUntil;
                    user.SecurityStamp = Guid.NewGuid().ToString();
                    await sessionStore.RevokeAllSessionsAsync(targetId.ToString(), cancellationToken: cancellationToken);
                }
            }

            db.AdminAuditLogs.Add(new AdminAuditLog
            {
                AdminUserId = adminId,
                TargetUserId = report.TargetUserId,
                Action = "ReviewReport",
                Reason = note,
                Detail = $"report={reportId};status={newStatus}",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthOperationResult.Success();
        });
    }

    public async Task<AuthOperationResult> AppealAsync(
        long userId, long reportId, string appealNote, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appealNote))
            return AuthOperationResult.Fail("ValidationFailed", "申诉说明不能为空");
        if (appealNote.Trim().Length > 2000)
            return AuthOperationResult.Fail("ValidationFailed", "申诉说明过长");

        var report = await db.UserReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
            return AuthOperationResult.Fail("NotFound", "举报不存在");
        if (report.TargetUserId != userId)
            return AuthOperationResult.Fail("Forbidden", "只能对自己相关的处置提出申诉");
        if (!TerminalStatuses.Contains(report.Status))
            return AuthOperationResult.Fail("InvalidTransition", "仅已处置或已驳回的案件可申诉");

        report.Status = UserReportStatus.Appealed;
        report.AppealNote = appealNote.Trim();
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return AuthOperationResult.Success();
    }

    private static bool CanTransition(UserReportStatus from, UserReportStatus to) => from switch
    {
        UserReportStatus.Open => to is UserReportStatus.Reviewed or UserReportStatus.ActionTaken or UserReportStatus.Rejected,
        UserReportStatus.Appealed => to is UserReportStatus.Reviewed or UserReportStatus.ActionTaken or UserReportStatus.Rejected,
        UserReportStatus.Reviewed => to is UserReportStatus.ActionTaken or UserReportStatus.Rejected,
        _ => false,
    };

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
