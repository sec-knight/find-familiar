namespace FindFamiliar.Runner;

/// <summary>
/// Mirrors the server's versioned runner-bridge JSON contracts (see
/// FindFamiliar.Server/Api/Runner/RunnerContracts.cs and ADR-0006). Property names and the
/// contract version are frozen and must stay in lockstep between the two projects.
/// </summary>
public static class RunnerProtocol
{
    public const int ContractVersion = 1;

    public const int MaxAssignmentMarkdownLength = 500_000;
    public const int MaxLongFieldLength = 12_000;
    public const int MaxSummaryLength = 4_000;
    public const int MaxArtifactTitleLength = 200;

    /// <summary>Maximum bytes read from the adapter's stdout/stderr before treating output as oversized.</summary>
    public const int MaxAdapterOutputBytes = 256 * 1024;
}

public sealed record AssignmentResponse(
    int ContractVersion,
    Guid TaskId,
    Guid SessionId,
    string Role,
    int ContextRevisionRead,
    string RolePrompt,
    string AssignmentMarkdown);

public sealed record ResultRequest(
    int ContractVersion,
    string Prompt,
    string RawOutput,
    string Summary,
    string ArtifactTitle,
    string ArtifactContent);

public sealed record CancelRequest(int ContractVersion, string Reason);

/// <summary>Sent to the adapter on stdin. The adapter cannot choose any of these fields.</summary>
public sealed record AdapterInvocation(
    int ContractVersion,
    Guid TaskId,
    Guid SessionId,
    string Role,
    string RolePrompt,
    string AssignmentMarkdown);

/// <summary>Read from the adapter's stdout. Contains only the result fields the capture service needs.</summary>
public sealed record AdapterResult(
    int ContractVersion,
    string RawOutput,
    string Summary,
    string ArtifactTitle,
    string ArtifactContent);
