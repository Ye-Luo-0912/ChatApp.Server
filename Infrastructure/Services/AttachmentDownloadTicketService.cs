using System.Security.Cryptography;
using Core.Caching;
using Core.Interfaces.Cache;
using Core.Models.Attachment;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 鉴权下载短时票：Redis JSON + TTL，原子 GETDEL 单次消费；绑定 userId+attachmentId。
/// </summary>
public interface IAttachmentDownloadTicketService
{
    Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 消费票。无效/过期/已用返回 null；调用方须再校验 userId/attachmentId。
    /// </summary>
    Task<AttachmentDownloadTicketPayload?> TryConsumeAsync(
        string ticket,
        CancellationToken cancellationToken = default);
}

public sealed record AttachmentDownloadTicketPayload(long UserId, string AttachmentId);

public sealed class AttachmentDownloadTicketService(
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    IOptions<AttachmentStorageOptions> options) : IAttachmentDownloadTicketService
{
    public async Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var minutes = Math.Clamp(options.Value.DownloadTicketMinutes, 1, 5);
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await cache.SetAsync(
                TicketKey(ticket),
                new AttachmentDownloadTicketPayload(userId, attachmentId),
                expires - DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return (ticket, expires);
    }

    public Task<AttachmentDownloadTicketPayload?> TryConsumeAsync(
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return Task.FromResult<AttachmentDownloadTicketPayload?>(null);

        return atomicCache.TryGetAndDeleteAsync<AttachmentDownloadTicketPayload>(
            TicketKey(ticket.Trim()), cancellationToken);
    }

    private static string TicketKey(string ticket) =>
        CacheConstants.AttachmentDownloadTicketPrefix + ticket;
}
