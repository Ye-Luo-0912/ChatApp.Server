using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 默认占位：未配置 MessageEvidence:RealtimeConnectionString 且无 NATS 总线时使用。
/// </summary>
public sealed class UnavailableMessageEvidenceProvider(ILogger<UnavailableMessageEvidenceProvider> logger)
    : IMessageEvidenceProvider
{
    public Task<MessageEvidenceSnapshot?> TryGetAsync(
        string messageId,
        long? requestingUserId = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "消息证据提供者未配置（需 MessageEvidence:RealtimeConnectionString 或 Realtime NATS），messageId={MessageId}",
            messageId);
        return Task.FromResult<MessageEvidenceSnapshot?>(null);
    }
}
