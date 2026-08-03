using System.Collections;

namespace FindFamiliar.Runner;

/// <summary>
/// Explicit runner invocation input. Task/session identity and the server base URL are ordinary
/// (non-secret) CLI arguments; the Familiar bearer token and the administrator-controlled adapter
/// path/arguments/timeout come only from environment variables, never from a CLI argument.
/// </summary>
public sealed record RunnerArguments(
    Uri BaseUrl,
    Guid TaskId,
    Guid SessionId,
    string FamiliarToken,
    string AdapterPath,
    IReadOnlyList<string> AdapterArguments,
    TimeSpan Timeout)
{
    public const string TokenVariable = "FAMILIAR_RUNNER_TOKEN";
    public const string AdapterPathVariable = "FAMILIAR_RUNNER_ADAPTER_PATH";
    public const string AdapterArgumentsVariable = "FAMILIAR_RUNNER_ADAPTER_ARGS";
    public const string TimeoutVariable = "FAMILIAR_RUNNER_TIMEOUT_SECONDS";

    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultTimeoutSeconds = 300;

    public static RunnerArguments? TryParse(string[] args, IDictionary environment, TextWriter diagnostics)
    {
        string? baseUrlRaw = null;
        string? taskIdRaw = null;
        string? sessionIdRaw = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-url" when i + 1 < args.Length:
                    baseUrlRaw = args[++i];
                    break;
                case "--task-id" when i + 1 < args.Length:
                    taskIdRaw = args[++i];
                    break;
                case "--session-id" when i + 1 < args.Length:
                    sessionIdRaw = args[++i];
                    break;
                default:
                    diagnostics.WriteLine($"runner: unrecognized or incomplete argument '{args[i]}'.");
                    return null;
            }
        }

        if (baseUrlRaw is null || taskIdRaw is null || sessionIdRaw is null)
        {
            diagnostics.WriteLine("runner: usage: --base-url <url> --task-id <guid> --session-id <guid>");
            return null;
        }

        if (!Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var baseUrl))
        {
            diagnostics.WriteLine("runner: --base-url must be an absolute URL.");
            return null;
        }

        if (!Guid.TryParse(taskIdRaw, out var taskId))
        {
            diagnostics.WriteLine("runner: --task-id must be a valid GUID.");
            return null;
        }

        if (!Guid.TryParse(sessionIdRaw, out var sessionId))
        {
            diagnostics.WriteLine("runner: --session-id must be a valid GUID.");
            return null;
        }

        var token = environment[TokenVariable] as string;
        if (string.IsNullOrWhiteSpace(token))
        {
            diagnostics.WriteLine($"runner: {TokenVariable} environment variable is required.");
            return null;
        }

        var adapterPath = environment[AdapterPathVariable] as string;
        if (string.IsNullOrWhiteSpace(adapterPath))
        {
            diagnostics.WriteLine($"runner: {AdapterPathVariable} environment variable is required.");
            return null;
        }

        var adapterArgumentsRaw = environment[AdapterArgumentsVariable] as string;
        var adapterArguments = string.IsNullOrWhiteSpace(adapterArgumentsRaw)
            ? Array.Empty<string>()
            : adapterArgumentsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var timeoutSeconds = DefaultTimeoutSeconds;
        var timeoutRaw = environment[TimeoutVariable] as string;
        if (!string.IsNullOrWhiteSpace(timeoutRaw))
        {
            if (!int.TryParse(timeoutRaw, out timeoutSeconds))
            {
                diagnostics.WriteLine($"runner: {TimeoutVariable} must be an integer number of seconds.");
                return null;
            }
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

        return new RunnerArguments(
            baseUrl,
            taskId,
            sessionId,
            token,
            adapterPath,
            adapterArguments,
            TimeSpan.FromSeconds(timeoutSeconds));
    }
}
