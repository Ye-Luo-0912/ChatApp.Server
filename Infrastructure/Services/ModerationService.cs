using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Moderation;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Services;

public sealed class ModerationService(
    UserDbContext db,
    ISecurityEventStore securityEventStore,
    IMessageEvidenceProvider messageEvidence,
    ILogger<ModerationService> logger,
    ISecurityVersionAdvancer? securityVersions = null,
    ISecurityMutationCoordinator? securityMutations = null,
    IAdminAuditWriter? adminAudit = null) : IModerationService
{
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            securityVersions ?? new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);

    private static readonly HashSet<UserReportStatus> TerminalStatuses =
        [UserReportStatus.Rejected, UserReportStatus.ActionTaken];
    private readonly IAdminAuditWriter? _adminAudit = adminAudit;

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

        string? evidenceSnapshot = null;
        string? evidenceBodyPreview = null;
        string? evidenceContentHash = null;
        if (targetType == UserReportTargetType.Message)
        {
            var evidence = await messageEvidence.TryGetAsync(
                targetMessageId!.Trim(), reporterId, cancellationToken);
            if (evidence is null)
                return AuthOperationResult.Fail("EvidenceUnavailable", "无法从消息服务获取可信证据，请稍后重试");

            // 举报人必须是发送方或接收方
            if (evidence.SenderUserId != reporterId && evidence.ReceiverUserId != reporterId)
                return AuthOperationResult.Fail("Forbidden", "只能举报自己参与的消息");

            // 目标用户：对方账号（举报人视角）
            var derivedTarget = evidence.SenderUserId == reporterId
                ? evidence.ReceiverUserId
                : evidence.SenderUserId;

            if (targetUserId is > 0 && targetUserId != derivedTarget && targetUserId != evidence.SenderUserId)
                return AuthOperationResult.Fail("TargetMismatch", "举报目标用户与消息参与方不一致");

            targetUserId = derivedTarget;

            if (targetUserId == reporterId)
                return AuthOperationResult.Fail("InvalidTarget", "不能举报自己");

            evidenceBodyPreview = LimitPreview(evidence.IsRecalled ? string.Empty : evidence.BodyText, 4000);
            evidenceContentHash = evidence.ContentHashSha256;
            evidenceSnapshot = JsonSerializer.Serialize(new
            {
                evidence.MessageId,
                evidence.SenderUserId,
                evidence.ReceiverUserId,
                SentAtUtc = evidence.SentAtUtc,
                evidence.EditVersion,
                evidence.EditedAtMs,
                evidence.IsRecalled,
                evidence.RecalledAtMs,
                Source = "message-service",
            });
        }
        else
        {
            if (targetUserId == reporterId)
                return AuthOperationResult.Fail("InvalidTarget", "不能举报自己");
        }

        if (targetUserId is > 0)
        {
            var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId, cancellationToken);
            if (!exists)
                return AuthOperationResult.Fail("TargetNotFound", "目标用户不存在");
        }

        var now = DateTimeOffset.UtcNow;
        var utcBucket = now.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var targetKey = targetType == UserReportTargetType.Message
            ? $"message:{targetMessageId!.Trim()}"
            : $"user:{targetUserId.GetValueOrDefault()}";
        var dedupeKey = $"{reporterId}:{(byte)targetType}:{targetKey}:{utcBucket}";
        var since = now.AddHours(-24);
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

        var report = new UserReport
        {
            ReporterId = reporterId,
            TargetType = targetType,
            TargetUserId = targetUserId,
            TargetMessageId = targetMessageId,
            EvidenceSnapshot = evidenceSnapshot,
            EvidenceBodyPreview = evidenceBodyPreview,
            EvidenceContentHash = evidenceContentHash,
            DedupeKey = dedupeKey,
            Reason = reason.Trim(),
            Detail = detail,
            Status = UserReportStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UserReports.Add(report);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresDbException.IsUniqueViolation(
                   ex, PostgresDbException.UserReportDedupeConstraint))
        {
            return AuthOperationResult.Fail("DuplicateReport", "当天已提交过相同举报");
        }

        await securityEventStore.RecordAsync(
            reporterId, SecurityEventType.ReportSubmitted,
            detail: $"report={report.Id} type={targetType}",
            cancellationToken: cancellationToken);

        logger.LogInformation("用户 {ReporterId} 提交举报 {ReportId}", reporterId, report.Id);
        return AuthOperationResult.Success();
    }

    public async Task<UserReportEvidenceDto?> GetEvidenceAsync(
        long adminUserId,
        long reportId,
        CancellationToken cancellationToken = default)
    {
        var evidence = await db.UserReports.AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => new UserReportEvidenceDto
            {
                ReportId = r.Id,
                ReporterId = r.ReporterId,
                TargetType = r.TargetType,
                TargetUserId = r.TargetUserId,
                TargetMessageId = r.TargetMessageId,
                EvidenceSnapshot = r.EvidenceSnapshot,
                BodyPreview = r.EvidenceBodyPreview,
                ContentHash = r.EvidenceContentHash,
                CapturedAt = r.CreatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (evidence is not null && _adminAudit is not null)
        {
            await _adminAudit.WriteAsync(
                adminUserId,
                evidence.TargetUserId,
                "ViewReportEvidence",
                reason: null,
                detail: $"report={reportId}",
                clientIp: null,
                cancellationToken).ConfigureAwait(false);
        }

        return evidence;
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
        if (newStatus == UserReportStatus.ActionTaken
            && banUntil is { } requestedBanUntil
            && requestedBanUntil <= DateTimeOffset.UtcNow)
        {
            return AuthOperationResult.Fail("ValidationFailed", "封禁截止时间必须晚于当前时间");
        }

        var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote is { Length: > 512 })
            return AuthOperationResult.Fail("ValidationFailed", "审核备注过长");

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.UserReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
            return AuthOperationResult.Fail("NotFound", "举报不存在");
        if (!CanTransition(report.Status, newStatus))
        {
            return AuthOperationResult.Fail(
                "InvalidTransition",
                $"不能从 {report.Status} 转到 {newStatus}");
        }

        var reviewedDetail = normalizedNote is null
            ? null
            : string.IsNullOrWhiteSpace(report.Detail)
                ? normalizedNote
                : $"{report.Detail}\n[admin] {normalizedNote}";
        if (reviewedDetail is { Length: > 2000 })
            return AuthOperationResult.Fail("ValidationFailed", "审核备注会超过举报详情长度限制");

        var stageSessionRevocation = newStatus == UserReportStatus.ActionTaken
                                     && banUntil.HasValue
                                     && report.TargetUserId.HasValue;
        var transition = db.UserReports.Where(
            r => r.Id == reportId && r.Status == report.Status);

        int transitioned;
        if (reviewedDetail is not null)
        {
            transitioned = stageSessionRevocation
                ? await transition.ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, newStatus)
                    .SetProperty(r => r.ReviewedByAdminId, adminId)
                    .SetProperty(r => r.UpdatedAt, now)
                    .SetProperty(r => r.Detail, reviewedDetail)
                    .SetProperty(r => r.BanUntil, banUntil), cancellationToken)
                : await transition.ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, newStatus)
                    .SetProperty(r => r.ReviewedByAdminId, adminId)
                    .SetProperty(r => r.UpdatedAt, now)
                    .SetProperty(r => r.Detail, reviewedDetail), cancellationToken);
        }
        else
        {
            transitioned = stageSessionRevocation
                ? await transition.ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, newStatus)
                    .SetProperty(r => r.ReviewedByAdminId, adminId)
                    .SetProperty(r => r.UpdatedAt, now)
                    .SetProperty(r => r.BanUntil, banUntil), cancellationToken)
                : await transition.ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, newStatus)
                    .SetProperty(r => r.ReviewedByAdminId, adminId)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        }

        if (transitioned != 1)
        {
            return AuthOperationResult.Fail(
                "Conflict",
                "举报状态已被其他审核员更新，请刷新后重试");
        }

        if (stageSessionRevocation)
        {
            var targetUserId = report.TargetUserId.GetValueOrDefault();
            var expectedBanUntil = banUntil.GetValueOrDefault();
            var securityStamp = Guid.NewGuid().ToString();
            var userUpdated = await db.Users
                .Where(u => u.Id == targetUserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.BanUntil, banUntil)
                    .SetProperty(u => u.SecurityStamp, securityStamp), cancellationToken);
            if (userUpdated != 1)
            {
                return AuthOperationResult.Fail(
                    "TargetNotFound",
                    "目标用户不存在");
            }

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    targetUserId,
                    SecurityEventType.AccountBanned,
                    $"report={reportId};banUntil={expectedBanUntil:O}",
                    static _ => Task.CompletedTask,
                    cancellationToken,
                    securityEvent => securityEvent.ActorUserId = adminId.ToString(),
                    new SecurityMutationOptions(EnqueueSessionRevocation: false))
                .ConfigureAwait(false);
            if (!mutation.Succeeded || !mutation.SecurityVersion.HasValue)
                return AuthOperationResult.Fail(
                    "TargetNotFound",
                    "目标用户不存在或安全版本不可再推进");

            db.ModerationSessionRevocationOutbox.Add(new ModerationSessionRevocationOutboxItem
            {
                SourceReportId = reportId,
                UserId = targetUserId,
                ExpectedSecurityVersion = mutation.SecurityVersion.Value,
                ExpectedBanUntil = expectedBanUntil,
                Status = ModerationSessionRevocationOutboxStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = adminId,
            TargetUserId = report.TargetUserId,
            Action = "ReviewReport",
            Reason = normalizedNote,
            Detail = $"report={reportId};status={newStatus}",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return AuthOperationResult.Success();
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

    private static string LimitPreview(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? string.Empty;

        var length = max;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }
}
