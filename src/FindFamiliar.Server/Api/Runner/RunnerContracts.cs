using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Api.Runner;

/// <summary>
/// Versioned, bounded JSON contracts for the runner bridge machine API. Property names are
/// frozen per ADR-0006 and serialized with the application's standard camelCase + string-enum
/// HTTP JSON options (see Program.cs's ConfigureHttpJsonOptions).
/// </summary>
public static class RunnerContracts
{
    public const int ContractVersion = 1;

    /// <summary>Maximum accepted size, in bytes, for a runner result/cancel request body.</summary>
    public const int MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Maximum accepted length, in characters, for rendered assignment Markdown.</summary>
    public const int MaxAssignmentMarkdownLength = 500_000;
}

public sealed record RunnerAssignmentResponse(
    int ContractVersion,
    Guid TaskId,
    Guid SessionId,
    AgentSessionRole Role,
    int ContextRevisionRead,
    string RolePrompt,
    string AssignmentMarkdown);

/// <summary>
/// Posted to the result endpoint. Task/session identity is taken from the route only — this
/// contract intentionally carries no ID fields, so there is nothing for the server to "trust or
/// reject" from the body.
/// </summary>
public sealed record RunnerResultRequest(
    int ContractVersion,
    string? Prompt,
    string? RawOutput,
    string? Summary,
    string? ArtifactTitle,
    string? ArtifactContent,
    Guid? ClaimId = null);

/// <summary>Posted to the cancel endpoint. Same route-authoritative-identity rule as the result contract.</summary>
public sealed record RunnerCancelRequest(
    int ContractVersion,
    string? Reason,
    Guid? ClaimId = null,
    RunnerFailureDiagnostic? Diagnostic = null);

public sealed record RunnerFailureDiagnostic(
    string? Category,
    int? AdapterExitCode,
    bool? ProviderLaunched,
    int? ProviderExitCode,
    string? Message);

/// <summary>
/// Posted by a worker to announce availability. Carries no repository path, adapter path, or other
/// machine-specific configuration — those stay on the worker host (ADR-0008).
/// </summary>
public sealed record WorkerHeartbeatRequestBody(
    int ContractVersion,
    string? WorkerKey,
    string? DisplayName,
    IReadOnlyList<string>? Capabilities);

public sealed record WorkerHeartbeatResponse(
    int ContractVersion,
    Guid WorkerId,
    bool Enabled,
    WorkerAvailability Availability);

/// <summary>
/// Posted by a worker to request work. <c>ProjectIds</c> is the set of projects the worker has a
/// local repository mapping for; the server stores none of them.
/// </summary>
public sealed record WorkerClaimRequestBody(
    int ContractVersion,
    string? WorkerKey,
    IReadOnlyList<Guid>? ProjectIds,
    int? LeaseSeconds);

public sealed record WorkerClaimRenewRequestBody(
    int ContractVersion,
    string? WorkerKey,
    Guid SessionId,
    Guid ClaimId,
    int? LeaseSeconds);

public sealed record WorkerClaimRenewResponse(
    int ContractVersion,
    Guid SessionId,
    Guid ClaimId,
    DateTime LeaseExpiresUtc);

/// <summary>
/// A granted claim, bundled with the same assignment payload the explicit assignment endpoint
/// returns, so a worker never needs a second round trip (and cannot observe a session between
/// claiming it and reading it).
/// </summary>
public sealed record WorkerClaimResponse(
    int ContractVersion,
    Guid WorkerId,
    Guid ClaimId,
    Guid ProjectId,
    Guid TaskId,
    Guid SessionId,
    AgentSessionRole Role,
    int ContextRevisionRead,
    string RolePrompt,
    string AssignmentMarkdown,
    DateTime ClaimedUtc,
    DateTime LeaseExpiresUtc);

public sealed record RunnerErrorResponse(int ContractVersion, string Message, IReadOnlyDictionary<string, string>? Errors = null)
{
    public static RunnerErrorResponse Create(string message, IReadOnlyDictionary<string, string>? errors = null) =>
        new(RunnerContracts.ContractVersion, message, errors);
}
