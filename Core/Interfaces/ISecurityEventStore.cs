using Core.Models.Security;

namespace Core.Interfaces;

/// <summary>安全事件写入。</summary>
public interface ISecurityEventStore
{
    /// <summary>同步关键事件（与业务同事务时由仓储写入；此处单独提交）。</summary>
    Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);

    Task RecordAsync(
        long? userId,
        SecurityEventType type,
        string? deviceId = null,
        string? clientIp = null,
        string? location = null,
        string? detail = null,
        string? actorUserId = null,
        CancellationToken cancellationToken = default,
        string? sessionId = null);

    /// <summary>批量写入；失败抛出。</summary>
    Task RecordManyAsync(IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登录热路径：挂起 durable audit-outbox rows 到 DbContext，不单独 SaveChanges。
    /// </summary>
    void StageLoginEvents(IReadOnlyList<SecurityEvent> events);

    /// <summary>
    /// 兼容旧调用方：写入 durable audit-outbox，失败只记日志不抛出。
    /// </summary>
    Task TryRecordLoginEventsAsync(IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken = default);
}
