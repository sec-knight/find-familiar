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
    string? ArtifactContent);

/// <summary>Posted to the cancel endpoint. Same route-authoritative-identity rule as the result contract.</summary>
public sealed record RunnerCancelRequest(int ContractVersion, string? Reason);

public sealed record RunnerErrorResponse(int ContractVersion, string Message, IReadOnlyDictionary<string, string>? Errors = null)
{
    public static RunnerErrorResponse Create(string message, IReadOnlyDictionary<string, string>? errors = null) =>
        new(RunnerContracts.ContractVersion, message, errors);
}
