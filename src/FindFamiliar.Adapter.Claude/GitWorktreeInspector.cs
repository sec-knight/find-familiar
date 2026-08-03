using System.Diagnostics;

namespace FindFamiliar.Adapter.Claude;

public enum WorktreeCleanliness
{
    Clean,
    Dirty,
    NotAWorktree,
    NotWorktreeRoot,
    NotDisposable,
    GitUnavailable
}

/// <summary>
/// Confirms an edit-mode target really is a clean, disposable git worktree before Claude is
/// allowed to modify it. Git is invoked directly with <c>UseShellExecute=false</c> and an
/// argument list — never through a shell.
///
/// "Inside a clean repository" is deliberately not sufficient. Git answers
/// <c>rev-parse --is-inside-work-tree</c> and <c>status --porcelain</c> identically from any
/// subdirectory of a primary checkout, so those two alone would happily grant edit rights over a
/// developer's main working copy. The target must additionally be the worktree root and must be a
/// linked worktree created by <c>git worktree add</c>.
/// </summary>
public static class GitWorktreeInspector
{
    public static WorktreeCleanliness Inspect(string worktree, TimeSpan timeout)
    {
        var inside = RunGit(worktree, ["rev-parse", "--is-inside-work-tree"], timeout);
        if (inside is null)
        {
            return WorktreeCleanliness.GitUnavailable;
        }

        if (inside.Value.ExitCode != 0 || !inside.Value.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return WorktreeCleanliness.NotAWorktree;
        }

        var toplevel = RunGit(worktree, ["rev-parse", "--show-toplevel"], timeout);
        if (toplevel is null)
        {
            return WorktreeCleanliness.GitUnavailable;
        }

        if (toplevel.Value.ExitCode != 0 || !IsSameDirectory(toplevel.Value.Stdout.Trim(), worktree))
        {
            return WorktreeCleanliness.NotWorktreeRoot;
        }

        // In a linked worktree the per-worktree git dir differs from the shared common dir; in a
        // primary checkout the two are the same path.
        var gitDir = RunGit(worktree, ["rev-parse", "--absolute-git-dir"], timeout);
        var commonDir = RunGit(worktree, ["rev-parse", "--path-format=absolute", "--git-common-dir"], timeout);
        if (gitDir is null || commonDir is null)
        {
            return WorktreeCleanliness.GitUnavailable;
        }

        if (gitDir.Value.ExitCode != 0 || commonDir.Value.ExitCode != 0)
        {
            return WorktreeCleanliness.NotAWorktree;
        }

        // Fail closed: "the two paths are not comparable" must mean "not proven disposable",
        // never "different, therefore linked".
        var disposable = TryCompareDirectories(gitDir.Value.Stdout.Trim(), commonDir.Value.Stdout.Trim());
        if (disposable is not false)
        {
            return WorktreeCleanliness.NotDisposable;
        }

        var status = RunGit(worktree, ["status", "--porcelain"], timeout);
        if (status is null)
        {
            return WorktreeCleanliness.GitUnavailable;
        }

        if (status.Value.ExitCode != 0)
        {
            return WorktreeCleanliness.NotAWorktree;
        }

        return string.IsNullOrWhiteSpace(status.Value.Stdout) ? WorktreeCleanliness.Clean : WorktreeCleanliness.Dirty;
    }

    private static bool IsSameDirectory(string left, string right) => TryCompareDirectories(left, right) == true;

    /// <summary>
    /// True/false when both paths are comparable, null when either cannot be normalized. Callers
    /// decide which way an indeterminate answer should fail.
    /// </summary>
    private static bool? TryCompareDirectories(string left, string right)
    {
        var leftSegments = WorktreePathPolicy.Normalize(left);
        var rightSegments = WorktreePathPolicy.Normalize(right);

        if (leftSegments is null || rightSegments is null)
        {
            return null;
        }

        return leftSegments.Count == rightSegments.Count
            && WorktreePathPolicy.IsContained(leftSegments, rightSegments);
    }

    /// <summary>
    /// Runs git with bounded, concurrently drained output. Draining stdout to completion before
    /// touching stderr would deadlock whenever git fills the stderr pipe buffer, and the deadlock
    /// would sit inside the read rather than the wait — making the timeout unreachable.
    /// </summary>
    private static (int ExitCode, string Stdout)? RunGit(string worktree, string[] arguments, TimeSpan timeout)
    {
        const int maxOutputBytes = 256 * 1024;

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = worktree,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = new CancellationTokenSource(timeout);

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxOutputBytes, timeoutCts.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, maxOutputBytes, timeoutCts.Token);

            try
            {
                process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            return (process.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();

        try
        {
            while (builder.Length < maxBytes)
            {
                var read = await reader.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
