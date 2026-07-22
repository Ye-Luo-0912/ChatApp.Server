namespace Core.Settings;

/// <summary>
/// Server 侧可选接入 Realtime NATS（账号清理 Saga 完成事件等）。
/// Url 为空则不注册 IRealtimeMessageBus / 消费 Worker。
/// </summary>
public sealed class RealtimeIntegrationHostOptions
{
    public const string SectionName = "RealtimeIntegration";

    public string Url { get; init; } = "";
    public string ClientName { get; init; } = "chatapp-server";
    public string InstanceId { get; init; } = Environment.MachineName;
    public string AccountCleanupSubject { get; init; } = "chat.realtime-events.account-deleted";
    public string AccountCleanupConsumerName { get; init; } = "chatapp-server-account-cleanup-saga";
    public string RealtimeEventsSubject { get; init; } = "chat.realtime-events";
    public string RealtimeEventsStream { get; init; } = "REALTIME_EVENTS";
    public string DeadLettersSubject { get; init; } = "chat.dead-letters";
    public string DeadLettersStream { get; init; } = "DEAD_LETTERS";
}
