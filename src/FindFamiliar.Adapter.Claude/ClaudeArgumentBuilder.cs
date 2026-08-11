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
            // Read, Grep and Glob and nothing else. The boundary this mode enforces is "cannot
            // change the repository", and it is enforced the same way edit mode enforces its own:
            // by what is absent from the schema. Edit, Write and Bash are absent, so there is no
            // tool path to a file change, a git commit, a push, or any process execution — the
            // permission mode is defence in depth on top of that, not the thing being relied on.
            //
            // This list was empty until a Planner session proved why that was wrong. No tools at
            // all does keep the repository safe, but it also leaves the model unable to read the
            // repository it was assigned to plan or review — and a model asked to plan a codebase
            // it cannot see does not decline, it invents one. That session exited zero, its result
            // validated, and the platform recorded a confident plan for a directory layout this
            // repository has never had. A read-only session that cannot read is not a safe session;
            // it is a session whose output is untethered from the repository, written into the
            // durable context this application exists to keep honest.
            //
            // The runtime's working directory is the worktree (ClaudeAdapterEngine passes it to
            // the executor), so these tools reach the assigned tree and nothing else without
            // --add-dir widening the boundary.
            arguments.Add("--tools");
            arguments.Add("Read,Grep,Glob");
            arguments.Add("--permission-mode");
            arguments.Add("plan");
        }
        else if (configuration.Mode == ClaudeAdapterMode.EditWorktree)
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
        else
        {
            // Host maintenance (ADR-0021). Bash is present here — it is the entire point of the
            // mode, since no combination of Edit and Write restarts a unit or reads SMART data off
            // a disk.
            arguments.Add("--tools");
            arguments.Add("Bash,Edit,Write,Read,Grep,Glob");

            // Pre-approval is expressed as an allow-list, never as a bypass. The distinction is not
            // cosmetic: --dangerously-skip-permissions and --permission-mode=bypassPermissions
            // disable the permission system for everything at once, including tools this mode never
            // asked for and any tool a future runtime version adds. An allow-list grants exactly the
            // named tools and leaves the mechanism itself intact, so ProhibitedFlags stays true of
            // every mode this adapter can emit — see the assertion in ClaudeAdapterUnitTests.
            arguments.Add("--allowedTools");
            arguments.Add("Bash");
            arguments.Add("Edit");
            arguments.Add("Write");
            arguments.Add("--permission-mode");
            arguments.Add("acceptEdits");
            arguments.Add("--add-dir");
            arguments.Add(configuration.Worktree);
        }

        return arguments;
    }
}
