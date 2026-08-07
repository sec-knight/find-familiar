namespace FindFamiliar.Runner;

/// <summary>
/// Everything needed to execute one already-obtained assignment. Both entry points produce this:
/// the explicit CLI invocation (which fetches the assignment for a human-supplied task/session) and
/// the worker loop (which receives the assignment inside its claim). Execution, failure
/// classification, durable cancellation and result submission therefore have exactly one
/// implementation.
/// </summary>
public sealed record RunnerExecutionRequest(
    Guid TaskId,
    Guid SessionId,
    string FamiliarToken,
    string AdapterPath,
    IReadOnlyList<string> AdapterArguments,
    TimeSpan Timeout,
    string RolePrompt,
    string AssignmentMarkdown,
    string Role,
    // Per-invocation adapter environment (repository worktree, allowed root, mode). Machine-local
    // values the server never sees; applied to the adapter child process only.
    IReadOnlyDictionary<string, string>? AdapterEnvironment = null,
    Guid? ClaimId = null,
    // The workspace this session may actually reach, stated into the execution packet before the
    // adapter runs. Supplied by the worker loop, which knows the project mapping; resolved from the
    // adapter environment when an explicit invocation did not supply one. Never null by the time the
    // adapter is launched — see RunnerEngine.ExecuteAssignmentAsync.
    WorkspaceContract? Workspace = null);
