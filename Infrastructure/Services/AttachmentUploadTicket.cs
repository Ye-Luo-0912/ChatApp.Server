namespace Infrastructure.Services;

/// <summary>Local/S3 共用的短时上传票载荷。</summary>
public sealed record AttachmentUploadTicket(
    long UserId,
    string AttachmentId,
    string ObjectKey,
    string ContentType,
    long ContentLength,
    string? OriginalName,
    string? ClientAttachmentId,
    long ExpiresAtUnixMs);

/// <summary>
/// 秒传去重票：Presign 命中已确认内容时签发。Confirm 时把源对象复制到本票
/// 的 ObjectKey（目标），客户端无需 PUT；复制后走与普通上传一致的确认/扫描流程。
/// </summary>
public sealed record AttachmentDedupUploadTicket(
    long UserId,
    string AttachmentId,
    string ObjectKey,
    string SourceObjectKey,
    string Sha256,
    string ContentType,
    long ContentLength,
    string? OriginalName,
    string? ClientAttachmentId,
    long ExpiresAtUnixMs);

internal static class AttachmentUploadTicketKeys
{
    private const string Prefix = "attachment:ticket:";
    private const string DedupPrefix = "attachment:dedup-ticket:";

    public static string Create(string ticket) => string.Concat(Prefix, ticket);

    public static string CreateDedup(string ticket) => string.Concat(DedupPrefix, ticket);
}
