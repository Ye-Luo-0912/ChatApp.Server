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

/// <summary>Result of delivering one durable scan projection.</summary>
public enum AttachmentScanProjectionProcessResult
{
    Completed,
    RetryScheduled,
    DeadLetter,
    LeaseLost,
}

/// <summary>Outcome of a projection write guarded by ProjectionId/ScanVersion.</summary>
public enum AttachmentProjectionWriteResult
{
    Applied,
    AlreadySuperseded,
    NotFound,
}

/// <summary>
/// Outcome of renewing a fenced background-job lease. A transient failure
/// leaves ownership ambiguous; the shared executor therefore cancels active
/// work fail-closed and lets the durable lease expire for retry.
/// </summary>
public enum LeaseRenewalResult
{
    Renewed,
    LeaseLost,
    TransientFailure,
}

/// <summary>
/// Result of the executable portion of a leased job. Most jobs return
/// <see cref="ExecuteAndFinalize"/> and let the shared executor perform the
/// fenced terminal update. A workflow that already committed its own fenced
/// transition can return <see cref="AlreadyFinalized"/> or
/// <see cref="RetryScheduled"/> without causing a second terminal write.
/// </summary>
public enum LeasedJobExecutionOutcome
{
    ExecuteAndFinalize,
    AlreadyFinalized,
    RetryScheduled,
    LeaseLost,
}
