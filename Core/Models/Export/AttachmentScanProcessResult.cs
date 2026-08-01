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

/// <summary>
/// Outcome of renewing a fenced background-job lease. Callers must only
/// cancel active work after <see cref="LeaseRenewalResult.LeaseLost"/> is confirmed; a transient
/// storage failure leaves the current lease ownership ambiguous.
/// </summary>
public enum LeaseRenewalResult
{
    Renewed,
    LeaseLost,
    TransientFailure,
}
