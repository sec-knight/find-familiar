using System.Collections;
using System.Text.Json;

namespace FindFamiliar.Adapter.Claude;

/// <summary>
/// Administrator-controlled local configuration. Every value here comes from the local environment
/// only — never from the adapter's stdin, never from a CLI argument, and never from assignment
/// content. This is what makes the executable, repository, worktree, and permission mode
/// unreachable from untrusted assignment text.
/// </summary>
public sealed record ClaudeAdapterConfiguration(
    string RuntimePath,
    string? Entrypoint,
    string Worktree,
    string AllowedRoot,
    ClaudeAdapterMode Mode,
    TimeSpan Timeout,
    IReadOnlyList<string> ExtraArguments)
{
    public const string RuntimePathVariable = "FAMILIAR_CLAUDE_RUNTIME_PATH";
    public const string EntrypointVariable = "FAMILIAR_CLAUDE_ENTRYPOINT";
    public const string WorktreeVariable = "FAMILIAR_CLAUDE_WORKTREE";
    public const string AllowedRootVariable = "FAMILIAR_CLAUDE_ALLOWED_ROOT";
    public const string ModeVariable = "FAMILIAR_CLAUDE_MODE";
    public const string TimeoutVariable = "FAMILIAR_CLAUDE_TIMEOUT_SECONDS";
    public const string ExtraArgumentsVariable = "FAMILIAR_CLAUDE_EXTRA_ARGS";

    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultTimeoutSeconds = 600;

    public static ClaudeAdapterConfiguration? TryParse(IDictionary environment, TextWriter diagnostics)
    {
        var runtimePath = Read(environment, RuntimePathVariable);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            diagnostics.WriteLine($"adapter: {RuntimePathVariable} is required.");
            return null;
        }

        if (!Path.IsPathFullyQualified(runtimePath))
        {
            diagnostics.WriteLine($"adapter: {RuntimePathVariable} must be an absolute path.");
            return null;
        }

        var entrypoint = Read(environment, EntrypointVariable);
        if (!string.IsNullOrWhiteSpace(entrypoint) && !Path.IsPathFullyQualified(entrypoint))
        {
            diagnostics.WriteLine($"adapter: {EntrypointVariable} must be an absolute path when set.");
            return null;
        }

        var worktree = Read(environment, WorktreeVariable);
        if (string.IsNullOrWhiteSpace(worktree) || !Path.IsPathFullyQualified(worktree))
        {
            diagnostics.WriteLine($"adapter: {WorktreeVariable} is required and must be an absolute path.");
            return null;
        }

        var allowedRoot = Read(environment, AllowedRootVariable);
        if (string.IsNullOrWhiteSpace(allowedRoot) || !Path.IsPathFullyQualified(allowedRoot))
        {
            diagnostics.WriteLine($"adapter: {AllowedRootVariable} is required and must be an absolute path.");
            return null;
        }

        var modeRaw = Read(environment, ModeVariable);
        if (string.IsNullOrWhiteSpace(modeRaw))
        {
            diagnostics.WriteLine($"adapter: {ModeVariable} is required (read-only or edit-worktree).");
            return null;
        }

        ClaudeAdapterMode mode;
        switch (modeRaw.Trim())
        {
            case "read-only":
                mode = ClaudeAdapterMode.ReadOnly;
                break;
            case "edit-worktree":
                mode = ClaudeAdapterMode.EditWorktree;
                break;
            default:
                diagnostics.WriteLine($"adapter: {ModeVariable} must be 'read-only' or 'edit-worktree'.");
                return null;
        }

        var timeoutSeconds = DefaultTimeoutSeconds;
        var timeoutRaw = Read(environment, TimeoutVariable);
        if (!string.IsNullOrWhiteSpace(timeoutRaw) && !int.TryParse(timeoutRaw, out timeoutSeconds))
        {
            diagnostics.WriteLine($"adapter: {TimeoutVariable} must be an integer number of seconds.");
            return null;
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

        // A JSON array keeps quoted values and embedded spaces intact. Whitespace-splitting a
        // command string is exactly the fragile Windows-path boundary this adapter exists to avoid.
        IReadOnlyList<string> extraArguments = [];
        var extraRaw = Read(environment, ExtraArgumentsVariable);
        if (!string.IsNullOrWhiteSpace(extraRaw))
        {
            try
            {
                extraArguments = JsonSerializer.Deserialize<string[]>(extraRaw) ?? [];
            }
            catch (JsonException)
            {
                diagnostics.WriteLine($"adapter: {ExtraArgumentsVariable} must be a JSON array of strings.");
                return null;
            }

            // A permission bypass is never acceptable, even from administrator-controlled local
            // configuration — the mode boundary is the whole point of this adapter.
            foreach (var argument in extraArguments)
            {
                if (ClaudeArgumentBuilder.ProhibitedFlags.Any(
                        prohibited => argument.Contains(prohibited, StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.WriteLine($"adapter: {ExtraArgumentsVariable} must not contain a permission bypass flag.");
                    return null;
                }
            }
        }

        return new ClaudeAdapterConfiguration(
            runtimePath,
            string.IsNullOrWhiteSpace(entrypoint) ? null : entrypoint,
            worktree,
            allowedRoot,
            mode,
            TimeSpan.FromSeconds(timeoutSeconds),
            extraArguments);
    }

    private static string? Read(IDictionary environment, string key) => environment[key] as string;
}
