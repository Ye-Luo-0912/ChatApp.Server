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

internal static class AttachmentUploadTicketKeys
{
    private const string Prefix = "attachment:ticket:";

    public static string Create(string ticket) => string.Concat(Prefix, ticket);
}
