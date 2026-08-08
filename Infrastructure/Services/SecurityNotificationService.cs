using Core.Interfaces;
using Core.Models.Notifications;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>安全/业务通知入队；由 <see cref="NotificationDispatchWorker"/> 投递。</summary>
public sealed class SecurityNotificationService(
    UserDbContext db,
    ILogger<SecurityNotificationService> logger) : ISecurityNotificationService
{
    public void StageNotify(long userId, string type, string title, string body, bool preferEmail)
    {
        var now = DateTimeOffset.UtcNow;
        db.NotificationOutbox.Add(new NotificationOutboxItem
        {
            UserId = userId,
            Type = Truncate(type, 64),
            Title = Truncate(title, 200),
            Body = Truncate(body, 2000),
            PreferEmail = preferEmail,
            Status = NotificationOutboxStatus.Pending,
            IdempotencyKey = Truncate(
                $"{userId}:{type}:{now.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}",
                88),
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
        });
    }

    public async Task NotifyAsync(
        long userId,
        string type,
        string title,
        string body,
        bool preferEmail,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"{userId}:{type}:{now:yyyyMMddHHmm}"
            : Truncate(idempotencyKey, 88);
        try
        {
            var exists = await db.NotificationOutbox.AsNoTracking().AnyAsync(
                x => x.IdempotencyKey == key
                     && (x.Status == NotificationOutboxStatus.Pending
                         || x.Status == NotificationOutboxStatus.Processing
                         || x.Status == NotificationOutboxStatus.Failed),
                cancellationToken);
            if (exists) return;

            db.NotificationOutbox.Add(new NotificationOutboxItem
            {
                UserId = userId,
                Type = Truncate(type, 64),
                Title = Truncate(title, 200),
                Body = Truncate(body, 2000),
                PreferEmail = preferEmail,
                Status = NotificationOutboxStatus.Pending,
                IdempotencyKey = key,
                CreatedAt = now,
                UpdatedAt = now,
                NextAttemptAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogDebug(ex, "通知 Outbox 幂等冲突 UserId={UserId} Type={Type}", userId, type);
            DetachAddedOutbox();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "通知入队失败 UserId={UserId} Type={Type}", userId, type);
            DetachAddedOutbox();
        }
    }

    private void DetachAddedOutbox()
    {
        foreach (var entry in db.ChangeTracker.Entries<NotificationOutboxItem>()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified)
                     .ToList())
            entry.State = EntityState.Detached;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
