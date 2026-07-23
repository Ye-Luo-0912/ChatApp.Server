namespace Core.Models.Export;

public sealed record ChatExportAttachmentItem(
    string MessageId,
    long ReceivedAtMs,
    string Url,
    string? Name,
    string? ContentType,
    long? SizeBytes,
    string Source);
