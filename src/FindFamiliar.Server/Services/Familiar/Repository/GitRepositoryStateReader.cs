using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// Reads a repository's current state by asking git. Read-only on every path.
/// </summary>
public interface IRepositoryStateReader
{
    /// <summary>
    /// The repository's state, or null when it could not be read.
    ///
    /// Null rather than an exception, because every caller's correct response is the same one:
    /// write nothing and leave the previous snapshot alone. A snapshot that cannot be taken is not
    /// an error condition of the application — it is a Tuesday on which the repository was locked.
    /// </summary>
    Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Four git invocations and no shell.
///
/// The commands are fixed literals with fixed arguments; nothing a user or a model can influence
/// reaches them. There is no shell in the chain at all — no pipeline into <c>cut</c> and <c>sort</c>,
/// no quoting to get wrong — because the two-level view is a three-line transformation in C# and a
/// shell invocation is a whole category of injection this does not need to have opinions about.
/// </summary>
public sealed class GitRepositoryStateReader(
    IOptions<RepositorySnapshotOptions> options,
    ILogger<GitRepositoryStateReader> logger) : IRepositoryStateReader
{
    private readonly RepositorySnapshotOptions _options = options.Value;

    public async Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured())
        {
            return null;
        }

        var repositoryPath = _options.RepositoryPath!;

        try
        {
            var branch = await RunAsync(repositoryPath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
            var head = await RunAsync(repositoryPath, cancellationToken, "rev-parse", "HEAD");
            var files = await RunAsync(repositoryPath, cancellationToken, "ls-files", "--full-name");
            var log = await RunAsync(
                repositoryPath,
                cancellationToken,
                "log",
                $"-{RepositoryState.RecentCommitCount}",
                "--oneline",
                "--no-color");

            if (branch is null || head is null || files is null || log is null)
            {
                return null;
            }

            // Sorted here rather than by piping git through sort: ordinal, so the same repository
            // composes identically whatever locale the host is set to.
            var trackedPaths = Lines(files).Order(StringComparer.Ordinal).ToList();

            return new RepositoryState(
                Lines(branch).FirstOrDefault() ?? "(unknown)",
                Lines(head).FirstOrDefault() ?? "(unknown)",
                trackedPaths,
                RepositoryState.TwoLevelView(trackedPaths),
                Lines(log).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Includes the case where git is not installed at all. The prior snapshot stands.
            logger.LogWarning(exception, "The repository state at {Path} could not be read.", repositoryPath);
            return null;
        }
    }

    /// <summary>
    /// One git command's standard output, or null when it failed, timed out, or was not git's to
    /// answer. Standard error is logged and never stored: it is an operator's diagnostic, not a fact
    /// about the repository, and a snapshot entry is read by a model.
    /// </summary>
    private async Task<string?> RunAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process();

        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // Bounds this invocation without touching the caller's token, so a timeout and a shutdown
        // stay distinguishable: one is logged and skipped, the other propagates.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.GitTimeout);

        process.Start();

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("git {Arguments} did not finish within the configured timeout.", string.Join(' ', arguments));
            return null;
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "git {Arguments} exited {ExitCode}: {Error}",
                string.Join(' ', arguments),
                process.ExitCode,
                (await standardError).Trim());
            return null;
        }

        return await standardOutput;
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone, which is the outcome that was wanted.
        }
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
