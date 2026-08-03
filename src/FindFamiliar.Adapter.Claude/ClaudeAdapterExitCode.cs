namespace FindFamiliar.Adapter.Claude;

/// <summary>
/// Stable, documented, non-secret adapter exit categories (ADR-0007), mirroring the runner's own
/// <c>RunnerExitCode</c> convention. A category never encodes a path, prompt, credential, or any
/// portion of Claude's output.
/// </summary>
public enum ClaudeAdapterExitCode
{
    /// <summary>One valid protocol-v1 result document was written to stdout.</summary>
    Success = 0,

    /// <summary>Local configuration is missing, malformed, or not administrator-valid.</summary>
    ConfigurationInvalid = 2,

    /// <summary>Stdin was absent, oversized, malformed, or failed protocol-v1 validation.</summary>
    InvocationInvalid = 3,

    /// <summary>The configured worktree failed allowed-root containment or symlink-escape checks.</summary>
    WorktreeRejected = 4,

    /// <summary>Edit mode requires a clean git worktree and the target was dirty or not a worktree.</summary>
    WorktreeNotClean = 5,

    /// <summary>The Claude runtime could not be started at all.</summary>
    RuntimeLaunchFailed = 6,

    /// <summary>The Claude runtime exceeded the configured total timeout and its tree was killed.</summary>
    RuntimeTimeout = 7,

    /// <summary>The Claude runtime exited with a non-zero status.</summary>
    RuntimeNonZeroExit = 8,

    /// <summary>Claude's output was oversized, malformed, an error envelope, or failed validation.</summary>
    RuntimeOutputInvalid = 9,

    /// <summary>Claude reported at least one blocked tool attempt; treated as a policy failure.</summary>
    PermissionDenialReported = 10
}
