using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FindFamiliar.Runner;

/// <summary>
/// Orchestrates one explicit runner invocation end to end: fetch assignment, run the configured
/// adapter, submit exactly one result. Pre-submission adapter failures cancel durably through the
/// same cancellation application service the web UI uses; a network failure after the result
/// request has been sent never triggers an automatic cancellation, because capture may already
/// have committed on the server.
/// </summary>
public sealed class RunnerEngine(HttpClient httpClient, AdapterProcessExecutor adapterExecutor, TextWriter diagnostics)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RunnerExitCode> RunAsync(RunnerArguments arguments, CancellationToken cancellationToken)
    {
        diagnostics.WriteLine($"runner: fetching assignment (task={arguments.TaskId}, session={arguments.SessionId}).");

        var assignment = await FetchAssignmentAsync(arguments, cancellationToken);
        if (assignment is null)
        {
            return RunnerExitCode.AssignmentFetchFailed;
        }

        if (!IsAssignmentValid(assignment, arguments))
        {
            diagnostics.WriteLine("runner: assignment failed contract/identity/size validation.");
            return RunnerExitCode.AssignmentInvalid;
        }

        diagnostics.WriteLine($"runner: assignment validated (role={assignment.Role}). Launching adapter.");

        return await ExecuteAssignmentAsync(
            new RunnerExecutionRequest(
                assignment.TaskId,
                assignment.SessionId,
                arguments.FamiliarToken,
                arguments.AdapterPath,
                arguments.AdapterArguments,
                arguments.Timeout,
                assignment.RolePrompt,
                assignment.AssignmentMarkdown,
                assignment.Role),
            cancellationToken);
    }

    /// <summary>
    /// Runs the adapter for one assignment and resolves the session exactly once — durable
    /// cancellation on any pre-submission adapter failure, or a single result submission on
    /// success. Shared verbatim by the explicit CLI invocation and the worker loop.
    /// </summary>
    public async Task<RunnerExitCode> ExecuteAssignmentAsync(
        RunnerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        // The workspace is stated to the session before anything runs, and stated here rather than at
        // either entry point so the explicit CLI invocation and the worker loop cannot diverge. A
        // Reviewer that resolved its scope from ambient environment while an Implementer used the
        // configured worktree is how correct work came to be reported as missing.
        var workspace = request.Workspace ?? WorkspaceContract.TryResolve(
            request.AdapterEnvironment,
            name => Environment.GetEnvironmentVariable(name));

        if (workspace is null)
        {
            // Fail closed. Letting the adapter inherit whatever the operator happened to export is
            // precisely the silent divergence this slice exists to remove, and a session that cannot
            // say where it is standing should not start.
            diagnostics.WriteLine(
                "runner: no authorized workspace could be resolved for this session. Configure a project "
                + "mapping in worker.json, or set FAMILIAR_CLAUDE_WORKTREE for an explicit invocation.");

            return RunnerExitCode.AssignmentInvalid;
        }

        var assignmentMarkdown = workspace.Augment(request.AssignmentMarkdown);

        diagnostics.WriteLine($"runner: workspace contract applied (root={workspace.WorkspaceRoot}, mode={workspace.Mode}).");

        var stdinPayload = new AdapterInvocation(
            RunnerProtocol.ContractVersion,
            request.TaskId,
            request.SessionId,
            request.Role,
            request.RolePrompt,
            assignmentMarkdown);
        var stdinJson = JsonSerializer.Serialize(stdinPayload, JsonOptions);

        var execution = await adapterExecutor.RunAsync(
            request.AdapterPath,
            request.AdapterArguments,
            stdinJson,
            request.Timeout,
            cancellationToken,
            environmentOverrides: request.AdapterEnvironment);

        var failure = ClassifyAdapterFailure(execution, out var adapterResult);
        if (failure is not null)
        {
            diagnostics.WriteLine(
                $"runner: adapter failure ({failure.CancellationCategory}, adapter={failure.Diagnostic.Category}, "
                + $"provider-launched={failure.Diagnostic.ProviderLaunched?.ToString() ?? "unknown"}). "
                + "Requesting durable cancellation.");
            var cancelled = await CancelDurablyAsync(request, failure, cancellationToken);
            return cancelled ? RunnerExitCode.CancelledAfterAdapterFailure : RunnerExitCode.CancellationFailed;
        }

        diagnostics.WriteLine("runner: adapter result validated. Submitting result.");

        return await SubmitResultAsync(request, adapterResult!, cancellationToken);
    }

    private async Task<AssignmentResponse?> FetchAssignmentAsync(RunnerArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/runner/tasks/{arguments.TaskId}/sessions/{arguments.SessionId}/assignment");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", arguments.FamiliarToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine($"runner: assignment request returned status {(int)response.StatusCode}.");
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<AssignmentResponse>(body, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            diagnostics.WriteLine("runner: assignment request failed (transport or parse error).");
            return null;
        }
    }

    private static bool IsAssignmentValid(AssignmentResponse assignment, RunnerArguments arguments)
    {
        return assignment.ContractVersion == RunnerProtocol.ContractVersion
            && assignment.TaskId == arguments.TaskId
            && assignment.SessionId == arguments.SessionId
            && !string.IsNullOrWhiteSpace(assignment.Role)
            && !string.IsNullOrWhiteSpace(assignment.RolePrompt)
            && !string.IsNullOrWhiteSpace(assignment.AssignmentMarkdown)
            && assignment.AssignmentMarkdown.Length <= RunnerProtocol.MaxAssignmentMarkdownLength;
    }

    private static AdapterFailureClassification? ClassifyAdapterFailure(
        AdapterExecutionResult execution,
        out AdapterResult? adapterResult)
    {
        adapterResult = null;

        if (execution.LaunchFailed)
        {
            return Failure(
                "adapter-launch-failed",
                "RunnerLaunchFailed",
                null,
                providerLaunched: null,
                providerExitCode: null,
                "The adapter process could not be launched.");
        }

        if (execution.TimedOut)
        {
            return Failure(
                "adapter-timeout",
                "RunnerTimeout",
                null,
                providerLaunched: null,
                providerExitCode: null,
                "The adapter exceeded its timeout and was terminated.");
        }

        if (execution.ExitCode != 0)
        {
            if (RunnerProtocol.TryParseAdapterDiagnostic(execution.StderrBytes, out var parsed) && parsed is not null)
            {
                return new AdapterFailureClassification(
                    "adapter-non-zero-exit",
                    new RunnerFailureDiagnostic(
                        parsed.Category,
                        parsed.AdapterExitCode,
                        parsed.ProviderLaunched,
                        parsed.ProviderExitCode,
                        parsed.Message));
            }

            return Failure(
                "adapter-non-zero-exit",
                CategoryForExitCode(execution.ExitCode),
                execution.ExitCode,
                ProviderLaunchedForExitCode(execution.ExitCode),
                null,
                "The adapter exited with a non-zero status.");
        }

        if (execution.StdoutOversized)
        {
            return Failure(
                "adapter-output-oversized",
                "RuntimeOutputInvalid",
                9,
                providerLaunched: true,
                providerExitCode: null,
                "The adapter result exceeded the bounded output limit.");
        }

        AdapterResult? parsedResult;
        try
        {
            parsedResult = ParseSingleJsonDocument<AdapterResult>(execution.StdoutBytes);
        }
        catch (JsonException)
        {
            return Failure(
                "adapter-output-malformed",
                "RuntimeOutputInvalid",
                9,
                providerLaunched: true,
                providerExitCode: null,
                "The adapter result was not one valid JSON document.");
        }

        if (parsedResult is null || !IsAdapterResultValid(parsedResult))
        {
            return Failure(
                "adapter-output-invalid",
                "RuntimeOutputInvalid",
                9,
                providerLaunched: true,
                providerExitCode: null,
                "The adapter result failed protocol validation.");
        }

        adapterResult = parsedResult;
        return null;
    }

    private static AdapterFailureClassification Failure(
        string cancellationCategory,
        string category,
        int? adapterExitCode,
        bool? providerLaunched,
        int? providerExitCode,
        string message) =>
        new(
            cancellationCategory,
            new RunnerFailureDiagnostic(
                category, adapterExitCode, providerLaunched, providerExitCode, message));

    private static string CategoryForExitCode(int? exitCode) => exitCode switch
    {
        2 => "ConfigurationInvalid",
        3 => "InvocationInvalid",
        4 => "WorktreeRejected",
        5 => "WorktreeNotClean",
        6 => "RuntimeLaunchFailed",
        7 => "RuntimeTimeout",
        8 => "RuntimeNonZeroExit",
        9 => "RuntimeOutputInvalid",
        10 => "PermissionDenialReported",
        _ => "AdapterNonZeroExit"
    };

    private static bool? ProviderLaunchedForExitCode(int? exitCode) => exitCode switch
    {
        >= 7 and <= 10 => true,
        2 or 3 or 4 or 5 or 6 => false,
        _ => null
    };

    private sealed record AdapterFailureClassification(
        string CancellationCategory,
        RunnerFailureDiagnostic Diagnostic);

    private static bool IsAdapterResultValid(AdapterResult result)
    {
        return result.ContractVersion == RunnerProtocol.ContractVersion
            && !string.IsNullOrWhiteSpace(result.RawOutput) && result.RawOutput.Length <= RunnerProtocol.MaxLongFieldLength
            && !string.IsNullOrWhiteSpace(result.Summary) && result.Summary.Length <= RunnerProtocol.MaxSummaryLength
            && !string.IsNullOrWhiteSpace(result.ArtifactTitle) && result.ArtifactTitle.Length <= RunnerProtocol.MaxArtifactTitleLength
            && !string.IsNullOrWhiteSpace(result.ArtifactContent) && result.ArtifactContent.Length <= RunnerProtocol.MaxLongFieldLength
            && IsCompleteArtifactValid(result);
    }

    /// <summary>
    /// The complete artifact is optional, so absence is valid. Present-but-out-of-bounds is not: a
    /// retained artifact shorter than the excerpt it supposedly contains, or a declared length below
    /// what was actually retained, would make the completeness report a lie, and the whole point of
    /// carrying it is that a reader may trust that report (ADR-0020).
    /// </summary>
    private static bool IsCompleteArtifactValid(AdapterResult result)
    {
        if (result.CompleteArtifactContent is null && result.CompleteArtifactLength is null)
        {
            return true;
        }

        return result.CompleteArtifactContent is { Length: > 0 } complete
            && complete.Length <= RunnerProtocol.MaxCompleteArtifactLength
            && result.CompleteArtifactLength is { } declared
            && declared >= complete.Length;
    }

    /// <summary>
    /// Deserializes exactly one JSON document from <paramref name="bytes"/>, rejecting empty
    /// input, a second concatenated document, or any trailing non-whitespace content.
    /// </summary>
    private static T? ParseSingleJsonDocument<T>(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new JsonException("Adapter produced no output.");
        }

        using var document = JsonDocument.Parse(bytes);

        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        reader.Skip();
        var consumed = checked((int)reader.BytesConsumed);

        for (var i = consumed; i < bytes.Length; i++)
        {
            if (!IsJsonWhitespace(bytes[i]))
            {
                throw new JsonException("Adapter produced more than one JSON document.");
            }
        }

        return document.Deserialize<T>(JsonOptions);
    }

    private static bool IsJsonWhitespace(byte value) => value is 0x20 or 0x09 or 0x0A or 0x0D;

    public async Task<RunnerExitCode> CancelBeforeAdapterFailureAsync(
        RunnerExecutionRequest request,
        RunnerFailureDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        var failure = new AdapterFailureClassification(
            "adapter-non-zero-exit",
            diagnostic);
        var cancelled = await CancelDurablyAsync(request, failure, cancellationToken);
        return cancelled ? RunnerExitCode.CancelledAfterAdapterFailure : RunnerExitCode.CancellationFailed;
    }

    private async Task<RunnerExitCode> SubmitResultAsync(
        RunnerExecutionRequest request,
        AdapterResult adapterResult,
        CancellationToken cancellationToken)
    {
        var payload = new ResultRequest(
            RunnerProtocol.ContractVersion,
            request.RolePrompt,
            adapterResult.RawOutput,
            adapterResult.Summary,
            adapterResult.ArtifactTitle,
            adapterResult.ArtifactContent,
            request.ClaimId,
            adapterResult.CompleteArtifactContent,
            adapterResult.CompleteArtifactLength);

        try
        {
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"api/runner/tasks/{request.TaskId}/sessions/{request.SessionId}/result")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.FamiliarToken);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine("runner: result captured successfully.");
                return RunnerExitCode.Success;
            }

            diagnostics.WriteLine($"runner: result submission rejected with status {(int)response.StatusCode}.");
            return RunnerExitCode.ResultSubmissionRejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            diagnostics.WriteLine(
                "runner: result submission outcome is ambiguous (transport failure after the request was sent). " +
                "Not cancelling automatically — capture may already have committed.");
            return RunnerExitCode.ResultSubmissionAmbiguous;
        }
    }

    private async Task<bool> CancelDurablyAsync(
        RunnerExecutionRequest request,
        AdapterFailureClassification failure,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"api/runner/tasks/{request.TaskId}/sessions/{request.SessionId}/cancel")
            {
                Content = JsonContent.Create(
                    new CancelRequest(
                        RunnerProtocol.ContractVersion,
                        $"Runner cancelled: {failure.CancellationCategory}.",
                        request.ClaimId,
                        failure.Diagnostic),
                    options: JsonOptions)
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.FamiliarToken);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine($"runner: durable cancellation recorded ({failure.CancellationCategory}).");
                return true;
            }

            diagnostics.WriteLine($"runner: cancellation request rejected with status {(int)response.StatusCode}.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            diagnostics.WriteLine("runner: cancellation request failed (transport failure).");
            return false;
        }
    }
}
