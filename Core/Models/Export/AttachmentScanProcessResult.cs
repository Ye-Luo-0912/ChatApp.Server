namespace Core.Models.Export;

/// <summary>
/// Result of processing a claimed attachment scan job. This separates a normal
/// retry from a lost lease so worker metrics do not treat backoff as contention.
/// </summary>
public enum AttachmentScanProcessResult
{
    ResultStaged,
    RetryScheduled,
    LeaseLost,
}
