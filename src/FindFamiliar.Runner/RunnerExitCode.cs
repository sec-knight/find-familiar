namespace FindFamiliar.Runner;

/// <summary>
/// Stable, documented runner exit categories (ADR-0006). Values are process exit codes.
/// </summary>
public enum RunnerExitCode
{
    /// <summary>The result was captured successfully.</summary>
    Success = 0,

    /// <summary>Bad arguments/environment (missing base URL, task/session ID, token, or adapter path).</summary>
    UsageError = 2,

    /// <summary>The assignment could not be fetched (transport failure or non-success HTTP status).</summary>
    AssignmentFetchFailed = 3,

    /// <summary>The fetched assignment failed contract/identity/size validation.</summary>
    AssignmentInvalid = 4,

    /// <summary>
    /// An adapter failure occurred before any result was submitted, and durable cancellation
    /// succeeded — the role is retryable through the normal Familiar work queue.
    /// </summary>
    CancelledAfterAdapterFailure = 5,

    /// <summary>
    /// An adapter failure occurred before any result was submitted, and cancellation itself also
    /// failed — the Started session is left visible for human recovery.
    /// </summary>
    CancellationFailed = 6,

    /// <summary>The server definitively rejected the result submission (for example 409/400).</summary>
    ResultSubmissionRejected = 7,

    /// <summary>
    /// The result submission's outcome is unknown because of a transport failure after the
    /// request was sent. The runner never auto-cancels in this case, because capture may already
    /// have committed.
    /// </summary>
    ResultSubmissionAmbiguous = 8
}
