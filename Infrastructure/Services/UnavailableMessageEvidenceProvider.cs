using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 默认占位：消息服务未接入前拒绝伪造客户端证据。
/// 接入实时/消息服务后替换为真实实现。
/// </summary>
public sealed class UnavailableMessageEvidenceProvider(ILogger<UnavailableMessageEvidenceProvider> logger)
    : IMessageEvidenceProvider
{
    public Task<MessageEvidenceSnapshot?> TryGetAsync(
        string messageId, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("消息证据提供者未配置，拒绝以客户端 detail 作为证据（messageId={MessageId}）", messageId);
        return Task.FromResult<MessageEvidenceSnapshot?>(null);
    }
}
