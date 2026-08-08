namespace Core.Interfaces;

/// <summary>Optional contract for avatar stores whose presigned PUT requires headers.</summary>
public interface IAvatarUploadHeadersProvider
{
    IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(string contentType);
}
