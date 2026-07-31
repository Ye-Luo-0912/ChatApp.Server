namespace Core.Interfaces;

/// <summary>Optional upload contract for object stores whose presigned PUT requires headers.</summary>
public interface IAttachmentUploadHeadersProvider
{
    IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(string contentType);
}
