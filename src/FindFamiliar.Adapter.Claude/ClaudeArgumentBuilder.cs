namespace FindFamiliar.Adapter.Claude;

/// <summary>
/// Builds the exact argv handed to the Claude runtime. Only flags verified against the installed
/// CLI (2.1.220) on the target machine are emitted, each as a separate argv element — nothing is
/// concatenated into a command string and no shell ever sees these values.
/// </summary>
public static class ClaudeArgumentBuilder
{
    /// <summary>Permission bypasses are never emitted; this list exists so tests can assert their absence.</summary>
    public static readonly IReadOnlyList<string> ProhibitedFlags =
    [
        "--dangerously-skip-permissions",
        "--permission-mode=bypassPermissions",
        "bypassPermissions"
    ];

    public static IReadOnlyList<string> Build(ClaudeAdapterConfiguration configuration)
    {
        var arguments = new List<string>();

        // node.exe + entrypoint shape: the entrypoint is the first argument to the runtime.
        if (configuration.Entrypoint is not null)
        {
            arguments.Add(configuration.Entrypoint);
        }

        // Operator extras are emitted before the policy flags, never after. The CLI resolves
        // repeated flags last-wins, so appending them would let an extra like
        // ["--tools","Bash"] silently widen the mode boundary this adapter exists to enforce.
        arguments.AddRange(configuration.ExtraArguments);

        // Noninteractive print mode; the prompt itself arrives on stdin, never as an argument.
        arguments.Add("-p");
        arguments.Add("--output-format");
        arguments.Add("json");
        arguments.Add("--no-session-persistence");

        if (configuration.Mode == ClaudeAdapterMode.ReadOnly)
        {
            // An empty --tools list removes the tools from the model's schema entirely, so
            // read-only does not depend on a permission prompt being answered. The plan
            // permission mode is defence in depth on top of that.
            arguments.Add("--tools");
            arguments.Add(string.Empty);
            arguments.Add("--permission-mode");
            arguments.Add("plan");
        }
        else
        {
            // Bash is deliberately excluded: without it there is no tool path to git commit,
            // push, or any other process execution from inside the model's turn.
            arguments.Add("--tools");
            arguments.Add("Edit,Write,Read,Grep,Glob");
            arguments.Add("--permission-mode");
            arguments.Add("acceptEdits");
            arguments.Add("--add-dir");
            arguments.Add(configuration.Worktree);
        }

        return arguments;
    }
}
