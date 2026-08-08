using System.Security.Cryptography;
using System.Text.Json;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Attachment;
using Core.Models.Token;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 鉴权下载短时票：Redis JSON + TTL，原子 GETDEL 单次消费；绑定 userId+attachmentId。
/// 产品语义是“服务端成功打开下载即消费”：本地文件响应或 S3 重定向已经返回后，
/// 客户端/网络中断不会恢复原票，客户端应重新签发下载票。
/// </summary>
public interface IAttachmentDownloadTicketService
{
    Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 消费票。无效/过期/已用返回 null；调用方须再校验 userId/attachmentId。
    /// 消费发生在响应打开前，成功打开（包括 S3 signed redirect）即视为消费。
    /// </summary>
    Task<AttachmentDownloadTicketPayload?> TryConsumeAsync(
        string ticket,
        CancellationToken cancellationToken = default);

    /// <summary>读取票而不消费，用于先校验绑定用户和附件。</summary>
    Task<AttachmentDownloadTicketPayload?> PeekAsync(
        string ticket,
        CancellationToken cancellationToken = default);
}

public sealed record AttachmentDownloadTicketPayload(long UserId, string AttachmentId);

public sealed class AttachmentDownloadTicketService(
    IOneTimeStateStore tickets,
    ISerializer serializer,
    IOptions<AttachmentStorageOptions> options) : IAttachmentDownloadTicketService
{
    public async Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var minutes = Math.Clamp(options.Value.DownloadTicketMinutes, 1, 5);
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        var ticket = TokenBufferEncoding.CreateHex(24);
        await tickets.IssueAsync(
                TicketKey(ticket),
                new AttachmentDownloadTicketPayload(userId, attachmentId),
                expires,
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

        return tickets.TryConsumeAsync<AttachmentDownloadTicketPayload>(
            TicketKey(ticket.Trim()), cancellationToken);
    }

    public async Task<AttachmentDownloadTicketPayload?> PeekAsync(
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        var raw = await tickets.PeekAsync(
                TicketKey(ticket.Trim()),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return serializer.Deserialize<AttachmentDownloadTicketPayload>(
                raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TicketKey(string ticket) =>
        CacheConstants.AttachmentDownloadTicketPrefix + ticket;
}
