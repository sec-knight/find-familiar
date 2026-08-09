using System.Text;
using System.Text.Json;

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

    /// <summary>Prefix for the bounded machine-readable adapter failure envelope on stderr.</summary>
    public const string AdapterDiagnosticPrefix = "find-familiar-adapter-diagnostic-v1:";

    public static string FormatAdapterDiagnostic(
        string category,
        int adapterExitCode,
        bool providerLaunched,
        int? providerExitCode,
        string message) =>
        AdapterDiagnosticPrefix + JsonSerializer.Serialize(new AdapterFailureDiagnostic(
            category, adapterExitCode, providerLaunched, providerExitCode, message));

    public static bool TryParseAdapterDiagnostic(
        byte[] stderrBytes,
        out AdapterFailureDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (stderrBytes.Length == 0)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(stderrBytes);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith(AdapterDiagnosticPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<AdapterFailureDiagnostic>(line[AdapterDiagnosticPrefix.Length..]);
                if (parsed is not null
                    && parsed.Category is { Length: > 0 and <= 80 }
                    && parsed.Message is { Length: > 0 and <= 300 }
                    && parsed.AdapterExitCode is >= 2 and <= 255
                    && (!parsed.ProviderExitCode.HasValue || parsed.ProviderExitCode.Value is >= 0 and <= 255))
                {
                    diagnostic = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Older adapters and malformed diagnostics fall back to the process exit code.
            }
        }

        return false;
    }
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
    string ArtifactContent,
    Guid? ClaimId = null);

public sealed record CancelRequest(
    int ContractVersion,
    string Reason,
    Guid? ClaimId = null,
    RunnerFailureDiagnostic? Diagnostic = null);

public sealed record RunnerFailureDiagnostic(
    string Category,
    int? AdapterExitCode,
    bool? ProviderLaunched,
    int? ProviderExitCode,
    string Message);

public sealed record AdapterFailureDiagnostic(
    string Category,
    int AdapterExitCode,
    bool ProviderLaunched,
    int? ProviderExitCode,
    string Message);

public sealed record WorkerHeartbeatRequestBody(
    int ContractVersion,
    string WorkerKey,
    string DisplayName,
    IReadOnlyList<string> Capabilities);

public sealed record WorkerHeartbeatResponse(
    int ContractVersion,
    Guid WorkerId,
    bool Enabled,
    string Availability);

public sealed record WorkerClaimRequestBody(
    int ContractVersion,
    string WorkerKey,
    IReadOnlyList<Guid> ProjectIds,
    int LeaseSeconds);

public sealed record WorkerClaimRenewRequestBody(
    int ContractVersion,
    string WorkerKey,
    Guid SessionId,
    Guid ClaimId,
    int LeaseSeconds);

public sealed record WorkerClaimRenewResponse(
    int ContractVersion,
    Guid SessionId,
    Guid ClaimId,
    DateTime LeaseExpiresUtc);

/// <summary>
/// A granted claim plus its assignment. The worker never chooses which session this is — the
/// server selected it atomically (ADR-0008).
/// </summary>
public sealed record WorkerClaimResponse(
    int ContractVersion,
    Guid WorkerId,
    Guid ClaimId,
    Guid ProjectId,
    Guid TaskId,
    Guid SessionId,
    string Role,
    int ContextRevisionRead,
    string RolePrompt,
    string AssignmentMarkdown,
    DateTime ClaimedUtc,
    DateTime LeaseExpiresUtc);

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
