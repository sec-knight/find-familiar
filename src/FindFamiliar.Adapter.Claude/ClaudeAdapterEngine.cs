using System.Collections;
using System.Text.Json;
using FindFamiliar.Runner;

namespace FindFamiliar.Adapter.Claude;

/// <summary>
/// One bounded adapter invocation end to end: validate stdin, validate administrator
/// configuration, prove the worktree is inside the allowed root, launch the verified Claude
/// runtime directly, and map its structured response into one protocol-v1 result document.
///
/// Every failure returns a stable category and writes nothing to stdout — there is no partial
/// success. Diagnostics are fixed non-secret strings; prompts, Claude output, environment
/// contents, and configured paths never appear in them.
/// </summary>
public sealed class ClaudeAdapterEngine(AdapterProcessExecutor processExecutor, TextWriter diagnostics)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClaudeAdapterExitCode> RunAsync(
        string stdin,
        IDictionary environment,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var parseOutcome = InvocationValidator.TryParse(stdin, out var invocation);
        if (parseOutcome != InvocationParseOutcome.Valid)
        {
            return Fail(
                ClaudeAdapterExitCode.InvocationInvalid,
                $"adapter: invocation rejected ({Describe(parseOutcome)}).",
                "InvocationInvalid",
                providerLaunched: false);
        }

        var configuration = ClaudeAdapterConfiguration.TryParse(environment, diagnostics);
        if (configuration is null)
        {
            return Fail(
                ClaudeAdapterExitCode.ConfigurationInvalid,
                "adapter: configuration was rejected.",
                "ConfigurationInvalid",
                providerLaunched: false);
        }

        var pathOutcome = WorktreePathPolicy.Evaluate(configuration.AllowedRoot, configuration.Worktree);
        if (pathOutcome != PathPolicyOutcome.Allowed)
        {
            return Fail(
                ClaudeAdapterExitCode.WorktreeRejected,
                $"adapter: worktree rejected ({pathOutcome}).",
                "WorktreeRejected",
                providerLaunched: false);
        }

        if (!File.Exists(configuration.RuntimePath))
        {
            return Fail(
                ClaudeAdapterExitCode.ConfigurationInvalid,
                "adapter: configured Claude runtime does not exist.",
                "ConfigurationInvalid",
                providerLaunched: false);
        }

        if (configuration.Entrypoint is not null && !File.Exists(configuration.Entrypoint))
        {
            return Fail(
                ClaudeAdapterExitCode.ConfigurationInvalid,
                "adapter: configured Claude entrypoint does not exist.",
                "ConfigurationInvalid",
                providerLaunched: false);
        }

        if (configuration.Mode == ClaudeAdapterMode.EditWorktree)
        {
            var cleanliness = GitWorktreeInspector.Inspect(configuration.Worktree, TimeSpan.FromSeconds(30));
            if (cleanliness != WorktreeCleanliness.Clean)
            {
                return Fail(
                    ClaudeAdapterExitCode.WorktreeNotClean,
                    $"adapter: edit mode requires a clean git worktree ({cleanliness}).",
                    "WorktreeNotClean",
                    providerLaunched: false);
            }
        }

        var prompt = ClaudePromptBuilder.Build(invocation!, configuration.Mode, configuration.Worktree);
        var arguments = ClaudeArgumentBuilder.Build(configuration);

        var execution = await processExecutor.RunAsync(
            configuration.RuntimePath,
            arguments,
            prompt,
            configuration.Timeout,
            cancellationToken,
            configuration.Worktree);

        if (execution.LaunchFailed)
        {
            return Fail(
                ClaudeAdapterExitCode.RuntimeLaunchFailed,
                "adapter: Claude runtime failed to launch.",
                "RuntimeLaunchFailed",
                providerLaunched: false);
        }

        if (execution.TimedOut)
        {
            return Fail(
                ClaudeAdapterExitCode.RuntimeTimeout,
                "adapter: Claude runtime timed out; process tree terminated.",
                "RuntimeTimeout",
                providerLaunched: true);
        }

        if (execution.ExitCode != 0)
        {
            return Fail(
                ClaudeAdapterExitCode.RuntimeNonZeroExit,
                "adapter: Claude runtime exited non-zero.",
                "RuntimeNonZeroExit",
                providerLaunched: true,
                providerExitCode: execution.ExitCode);
        }

        if (execution.StdoutOversized)
        {
            return Fail(
                ClaudeAdapterExitCode.RuntimeOutputInvalid,
                "adapter: Claude runtime output exceeded the bounded read limit.",
                "RuntimeOutputInvalid",
                providerLaunched: true);
        }

        var claudeStdout = System.Text.Encoding.UTF8.GetString(execution.StdoutBytes);
        var resultOutcome = ClaudeResultParser.TryParse(claudeStdout, out var adapterResult);

        if (resultOutcome == ClaudeResultOutcome.PermissionDenied)
        {
            return Fail(
                ClaudeAdapterExitCode.PermissionDenialReported,
                "adapter: Claude reported a blocked tool attempt; treating as policy failure.",
                "PermissionDenialReported",
                providerLaunched: true);
        }

        if (resultOutcome != ClaudeResultOutcome.Valid)
        {
            return Fail(
                ClaudeAdapterExitCode.RuntimeOutputInvalid,
                $"adapter: Claude output rejected ({resultOutcome}).",
                "RuntimeOutputInvalid",
                providerLaunched: true);
        }

        await stdout.WriteAsync(JsonSerializer.Serialize(adapterResult, JsonOptions));
        return ClaudeAdapterExitCode.Success;
    }

    private ClaudeAdapterExitCode Fail(
        ClaudeAdapterExitCode exitCode,
        string message,
        string category,
        bool providerLaunched,
        int? providerExitCode = null)
    {
        diagnostics.WriteLine(message);
        diagnostics.WriteLine(RunnerProtocol.FormatAdapterDiagnostic(
            category, (int)exitCode, providerLaunched, providerExitCode, message));
        return exitCode;
    }

    private static string Describe(InvocationParseOutcome outcome) => outcome switch
    {
        InvocationParseOutcome.Empty => "empty stdin",
        InvocationParseOutcome.Oversized => "stdin exceeded the bounded limit",
        InvocationParseOutcome.Malformed => "malformed JSON",
        InvocationParseOutcome.MultipleDocuments => "more than one JSON document",
        InvocationParseOutcome.UnsupportedContractVersion => "unsupported contract version",
        InvocationParseOutcome.MissingFields => "missing required fields",
        InvocationParseOutcome.AssignmentTooLong => "assignment exceeded the protocol limit",
        _ => "invalid"
    };
}
