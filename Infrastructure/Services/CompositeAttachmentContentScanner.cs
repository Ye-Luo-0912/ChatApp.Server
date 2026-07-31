using Core.Interfaces;

namespace Infrastructure.Services;

public sealed class CompositeAttachmentContentScanner(
    IAttachmentContentScanner policy,
    IAttachmentContentScanner malware) : IAttachmentContentScanner
{
    public async Task<AttachmentContentScanResult> ScanAsync(
        Stream content,
        string? sniffedContentType,
        string? originalName,
        CancellationToken cancellationToken = default)
    {
        var policyResult = await policy.ScanAsync(
                content, sniffedContentType, originalName, cancellationToken)
            .ConfigureAwait(false);
        if (!policyResult.Allowed || policyResult.IsTransient)
            return policyResult;

        if (content.CanSeek)
            content.Position = 0;
        var malwareResult = await malware.ScanAsync(
                content, sniffedContentType, originalName, cancellationToken)
            .ConfigureAwait(false);
        return malwareResult with
        {
            EngineName = malwareResult.EngineName ?? policyResult.EngineName,
            EngineVersion = malwareResult.EngineVersion ?? policyResult.EngineVersion,
        };
    }
}
