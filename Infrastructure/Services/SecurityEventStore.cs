using Core.Interfaces;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class SecurityEventStore(
    UserDbContext db,
    ILogger<SecurityEventStore> logger) : ISecurityEventStore
{
    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        db.SecurityEvents.Add(securityEvent);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task RecordAsync(
        long? userId,
        SecurityEventType type,
        string? deviceId = null,
        string? clientIp = null,
        string? location = null,
        string? detail = null,
        string? actorUserId = null,
        CancellationToken cancellationToken = default,
        string? sessionId = null)
    {
        return RecordAsync(new SecurityEvent
        {
            UserId = userId,
            EventType = type,
            DeviceId = deviceId,
            ClientIp = clientIp,
            Location = location,
            Detail = detail,
            ActorUserId = actorUserId,
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    public async Task RecordManyAsync(IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;
        db.SecurityEvents.AddRange(events);
        await db.SaveChangesAsync(cancellationToken);
    }

    public void StageLoginEvents(IReadOnlyList<SecurityEvent> events)
    {
        if (events.Count == 0) return;
        db.LoginAuditOutbox.AddRange(events.Select(ToLoginAuditOutbox));
    }

    public async Task TryRecordLoginEventsAsync(
        IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;
        try
        {
            db.LoginAuditOutbox.AddRange(events.Select(ToLoginAuditOutbox));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "登录安全事件写入失败（不影响登录结果） Count={Count}", events.Count);
            foreach (var entry in db.ChangeTracker.Entries<SecurityEvent>()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static LoginAuditOutboxItem ToLoginAuditOutbox(SecurityEvent securityEvent)
        => new()
        {
            UserId = securityEvent.UserId,
            EventType = securityEvent.EventType,
            DeviceId = securityEvent.DeviceId,
            SessionId = securityEvent.SessionId,
            ClientIp = securityEvent.ClientIp,
            Location = securityEvent.Location,
            Detail = securityEvent.Detail,
            ActorUserId = securityEvent.ActorUserId,
            CreatedAt = securityEvent.CreatedAt,
            UpdatedAt = securityEvent.CreatedAt,
        };
}
