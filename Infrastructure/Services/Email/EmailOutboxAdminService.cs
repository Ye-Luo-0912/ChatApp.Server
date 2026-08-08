using Core.Interfaces;
using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Email;

/// <summary>
/// 管理端邮件 Outbox 查询与重试。Controller 不直接持有 DbContext，
/// 重试条件在数据库 UPDATE 中再次校验，避免覆盖并发领取。
/// </summary>
public sealed class EmailOutboxAdminService(UserDbContext db) : IEmailOutboxAdminService
{
    public async Task<IReadOnlyList<EmailOutboxAdminItemDto>> ListDeadAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        return await db.EmailOutbox
            .AsNoTracking()
            .Where(x => x.Status == EmailOutboxStatus.Dead)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(limit)
            .Select(x => new EmailOutboxAdminItemDto(
                x.Id,
                x.To,
                x.Subject,
                x.EmailType,
                x.AttemptCount,
                x.LastError,
                x.UpdatedAt,
                x.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            return false;

        var now = DateTime.UtcNow;
        var updated = await db.EmailOutbox
            .Where(x => x.Id == id
                        && (x.Status == EmailOutboxStatus.Dead
                            || x.Status == EmailOutboxStatus.Failed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Pending)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.LastError, (string?)null), cancellationToken)
            .ConfigureAwait(false);

        return updated == 1;
    }
}
