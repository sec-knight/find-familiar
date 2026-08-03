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
            diagnostics.WriteLine($"adapter: invocation rejected ({Describe(parseOutcome)}).");
            return ClaudeAdapterExitCode.InvocationInvalid;
        }

        var configuration = ClaudeAdapterConfiguration.TryParse(environment, diagnostics);
        if (configuration is null)
        {
            return ClaudeAdapterExitCode.ConfigurationInvalid;
        }

        var pathOutcome = WorktreePathPolicy.Evaluate(configuration.AllowedRoot, configuration.Worktree);
        if (pathOutcome != PathPolicyOutcome.Allowed)
        {
            diagnostics.WriteLine($"adapter: worktree rejected ({pathOutcome}).");
            return ClaudeAdapterExitCode.WorktreeRejected;
        }

        if (!File.Exists(configuration.RuntimePath))
        {
            diagnostics.WriteLine("adapter: configured Claude runtime does not exist.");
            return ClaudeAdapterExitCode.ConfigurationInvalid;
        }

        if (configuration.Entrypoint is not null && !File.Exists(configuration.Entrypoint))
        {
            diagnostics.WriteLine("adapter: configured Claude entrypoint does not exist.");
            return ClaudeAdapterExitCode.ConfigurationInvalid;
        }

        if (configuration.Mode == ClaudeAdapterMode.EditWorktree)
        {
            var cleanliness = GitWorktreeInspector.Inspect(configuration.Worktree, TimeSpan.FromSeconds(30));
            if (cleanliness != WorktreeCleanliness.Clean)
            {
                diagnostics.WriteLine($"adapter: edit mode requires a clean git worktree ({cleanliness}).");
                return ClaudeAdapterExitCode.WorktreeNotClean;
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
            diagnostics.WriteLine("adapter: Claude runtime failed to launch.");
            return ClaudeAdapterExitCode.RuntimeLaunchFailed;
        }

        if (execution.TimedOut)
        {
            diagnostics.WriteLine("adapter: Claude runtime timed out; process tree terminated.");
            return ClaudeAdapterExitCode.RuntimeTimeout;
        }

        if (execution.ExitCode != 0)
        {
            diagnostics.WriteLine("adapter: Claude runtime exited non-zero.");
            return ClaudeAdapterExitCode.RuntimeNonZeroExit;
        }

        if (execution.StdoutOversized)
        {
            diagnostics.WriteLine("adapter: Claude runtime output exceeded the bounded read limit.");
            return ClaudeAdapterExitCode.RuntimeOutputInvalid;
        }

        var claudeStdout = System.Text.Encoding.UTF8.GetString(execution.StdoutBytes);
        var resultOutcome = ClaudeResultParser.TryParse(claudeStdout, out var adapterResult);

        if (resultOutcome == ClaudeResultOutcome.PermissionDenied)
        {
            diagnostics.WriteLine("adapter: Claude reported a blocked tool attempt; treating as policy failure.");
            return ClaudeAdapterExitCode.PermissionDenialReported;
        }

        if (resultOutcome != ClaudeResultOutcome.Valid)
        {
            diagnostics.WriteLine($"adapter: Claude output rejected ({resultOutcome}).");
            return ClaudeAdapterExitCode.RuntimeOutputInvalid;
        }

        await stdout.WriteAsync(JsonSerializer.Serialize(adapterResult, JsonOptions));
        return ClaudeAdapterExitCode.Success;
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
